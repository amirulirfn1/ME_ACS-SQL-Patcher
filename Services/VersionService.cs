using System.IO;
using System.Text.Json;
using MagDbPatcher.Models;

namespace MagDbPatcher.Services;

public class VersionService
{
    private VersionConfig _config = new();
    private PatcherConfig _patcherConfig = new();
    private readonly string _patchesFolder;
    private readonly string _patchesFolderRoot;
    private readonly string _configPath;
    private readonly string _patcherConfigPath;
    private readonly List<string> _nonFatalDiagnostics = new();
    private readonly VersionGraphService _versionGraphService = new();
    private readonly VersionConfigRepository _configRepository = new();
    private readonly PatchAutoGenerationService _patchAutoGenerationService = new();

    public VersionService(string patchesFolder)
    {
        _patchesFolder = patchesFolder;
        _patchesFolderRoot = EnsureTrailingSeparator(Path.GetFullPath(_patchesFolder));
        _configPath = Path.Combine(_patchesFolder, "versions.json");
        _patcherConfigPath = Path.Combine(_patchesFolder, "patcher.config.json");
    }

    public ConfigValidationResult LastValidationResult { get; private set; } = new();
    public IReadOnlyList<string> NonFatalDiagnostics => _nonFatalDiagnostics;

    public async Task LoadVersionsAsync()
    {
        _nonFatalDiagnostics.Clear();
        _patcherConfig = await LoadPatcherConfigAsync();

        _config = await _configRepository.LoadAsync(_configPath);

        var normalizedChanged = NormalizeConfig();
        normalizedChanged |= ApplyVersionOrdering();
        LastValidationResult = ValidateConfig();

        // Auto-sync with folder structure
        normalizedChanged |= SyncWithFolders();

        normalizedChanged |= NormalizeConfig();
        normalizedChanged |= ApplyVersionOrdering();
        normalizedChanged |= AutoGeneratePatches();

        LastValidationResult = ValidateConfig();

        if (normalizedChanged)
            await SaveConfigAsync();
    }

    private async Task<PatcherConfig> LoadPatcherConfigAsync()
    {
        if (!File.Exists(_patcherConfigPath))
        {
            return new PatcherConfig
            {
                SchemaVersion = 1,
                VersionOrdering = new VersionOrderingConfig { Mode = "semantic_with_optional_buildDate" },
                AutoGenerate = new AutoGenerateConfig
                {
                    BuildVersionPattern = "-",
                    Rules = new List<AutoGenerateRule>
                    {
                        new AutoGenerateRule { Type = "stable_to_build_same_major" },
                        new AutoGenerateRule
                        {
                            Type = "from_versions_to_build_major",
                            FromVersions = new List<string> { "6.5" },
                            ToMajor = 7
                        }
                    }
                }
            };
        }

        try
        {
            var cfg = await _configRepository.LoadPatcherConfigAsync(_patcherConfigPath);
            cfg.VersionOrdering ??= new VersionOrderingConfig();
            cfg.AutoGenerate ??= new AutoGenerateConfig();
            cfg.AutoGenerate.Rules ??= new List<AutoGenerateRule>();
            if (string.IsNullOrWhiteSpace(cfg.AutoGenerate.BuildVersionPattern))
                cfg.AutoGenerate.BuildVersionPattern = "-";
            if (string.IsNullOrWhiteSpace(cfg.VersionOrdering.Mode))
                cfg.VersionOrdering.Mode = "semantic_with_optional_buildDate";
            return cfg;
        }
        catch
        {
            return new PatcherConfig();
        }
    }

    /// <summary>
    /// Scans the patches folder and auto-discovers new version folders and scripts.
    /// Updates versions.json to match the folder structure.
    /// </summary>
    private bool SyncWithFolders()
    {
        if (!Directory.Exists(_patchesFolder)) return false;

        var folders = Directory.GetDirectories(_patchesFolder)
            .Select(d => Path.GetFileName(d))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        var changed = false;
        var maxOrder = _config.Versions.Any() ? _config.Versions.Max(v => v.Order) : 0;
        var newVersions = new List<VersionInfo>();

        foreach (var folderName in folders)
        {
            // Check if version exists in config
            var existingVersion = _config.Versions.FirstOrDefault(v =>
                string.Equals(v.Id, folderName, StringComparison.OrdinalIgnoreCase));

            if (existingVersion == null)
            {
                // New folder detected - add as version
                maxOrder++;
                var newVersion = new VersionInfo
                {
                    Id = folderName,
                    Name = folderName,
                    UpgradesTo = null,
                    Order = maxOrder
                };
                _config.Versions.Add(newVersion);
                newVersions.Add(newVersion);
                changed = true;
            }
        }

        if (newVersions.Count > 0)
        {
            foreach (var newVersion in newVersions.OrderBy(v => v.Order))
            {
                var major = GetMajorVersion(newVersion.Id);
                if (major == null) continue;

                var tail = _config.Versions
                    .Where(v => !string.Equals(v.Id, newVersion.Id, StringComparison.OrdinalIgnoreCase))
                    .Where(v => GetMajorVersion(v.Id) == major)
                    .OrderByDescending(v => v.Order)
                    .FirstOrDefault(v => string.IsNullOrWhiteSpace(v.UpgradesTo));

                if (tail != null)
                {
                    tail.UpgradesTo = newVersion.Id;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool AutoGeneratePatches()
    {
        var changed = false;
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var scriptsByVersion = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _config.Versions)
        {
            scriptsByVersion[v.Id] = GetScriptsInFolder(v.Id);
        }

        foreach (var v in _config.Versions)
        {
            if (string.IsNullOrWhiteSpace(v.UpgradesTo))
                continue;

            if (EnsureAutoPatch(v.Id, v.UpgradesTo!, scriptsByVersion, desired))
                changed = true;
        }

        // Direct jump: stable 7.x → build 7.x (id contains '-')
        var rules = _patcherConfig.AutoGenerate.Rules ?? new List<AutoGenerateRule>();
        var buildPattern = string.IsNullOrWhiteSpace(_patcherConfig.AutoGenerate.BuildVersionPattern)
            ? "-"
            : _patcherConfig.AutoGenerate.BuildVersionPattern;

        var stableToBuildEnabled = rules.Any(r =>
            string.Equals((r.Type ?? "").Trim(), "stable_to_build_same_major", StringComparison.OrdinalIgnoreCase));

        var fromToBuildRules = rules
            .Where(r => string.Equals((r.Type ?? "").Trim(), "from_versions_to_build_major", StringComparison.OrdinalIgnoreCase))
            .Where(r => r.ToMajor != null && r.FromVersions != null && r.FromVersions.Count > 0)
            .ToList();

        var stable7 = _config.Versions
            .Where(v => !IsBuildVersion(v.Id, buildPattern))
            .OrderBy(v => v.Order)
            .ToList();

        var build7 = _config.Versions
            .Where(v => IsBuildVersion(v.Id, buildPattern))
            .OrderBy(v => v.Order)
            .ToList();

        foreach (var target in build7)
        {
            var targetMajor = GetMajorVersion(target.Id);

            if (stableToBuildEnabled && targetMajor != null)
            {
                foreach (var source in stable7.Where(s =>
                             GetMajorVersion(s.Id) == targetMajor && s.Order < target.Order))
                {
                    if (EnsureAutoPatch(source.Id, target.Id, scriptsByVersion, desired))
                        changed = true;
                }
            }

            // Keep direct 6.5 → 7.x build behavior
            if (targetMajor != null)
            {
                foreach (var rule in fromToBuildRules.Where(r => r.ToMajor == targetMajor))
                {
                    foreach (var from in rule.FromVersions!.Select(NormalizeVersionId))
                    {
                        var fromV = _config.Versions.FirstOrDefault(v =>
                            string.Equals(NormalizeVersionId(v.Id), from, StringComparison.OrdinalIgnoreCase));
                        if (fromV == null) continue;

                        if (target.Order <= fromV.Order)
                            continue;

                        if (EnsureAutoPatch(from, target.Id, scriptsByVersion, desired))
                            changed = true;
                    }
                }
            }
        }

        // Direct-jump rule for 6.5+ sources:
        // any selected higher target should be reachable in a single step.
        var orderedVersions = _config.Versions
            .OrderBy(v => v.Order)
            .ToList();

        foreach (var source in orderedVersions.Where(v => IsVersionAtLeastSixFive(v.Id)))
        {
            foreach (var target in orderedVersions.Where(v => v.Order > source.Order))
            {
                if (EnsureAutoPatch(source.Id, target.Id, scriptsByVersion, desired))
                    changed = true;
            }
        }

        // Remove stale auto-generated patches
        var removed = _config.Patches.RemoveAll(p =>
            p.AutoGenerated && !desired.Contains(Key(p.From, p.To)));
        if (removed > 0) changed = true;

        return changed;
    }

    private bool EnsureAutoPatch(
        string from,
        string to,
        Dictionary<string, List<string>> scriptsByVersion,
        HashSet<string> desired)
    {
        return _patchAutoGenerationService.EnsureAutoPatch(_config.Patches, from, to, scriptsByVersion, desired);
    }

    private List<string> GetScriptsInFolder(string versionId)
        => _patchAutoGenerationService.GetScriptsInFolder(_patchesFolder, versionId);

    private static bool IsBuildVersion(string id, string buildPattern) =>
        PatchAutoGenerationService.IsBuildVersion(id, buildPattern);

    private static int? GetMajorVersion(string id) =>
        PatchAutoGenerationService.GetMajorVersion(id);

    private static string Key(string from, string to) =>
        PatchAutoGenerationService.Key(from, to);

    private static bool IsVersionAtLeastSixFive(string id)
    {
        var key = ParseVersionKey(id);
        if (key.Major > 6) return true;
        if (key.Major < 6) return false;
        return key.Minor >= 5;
    }

    private bool ApplyVersionOrdering()
    {
        var mode = (_patcherConfig.VersionOrdering.Mode ?? "").Trim();
        if (!mode.Equals("semantic_with_optional_buildDate", StringComparison.OrdinalIgnoreCase))
            return false;

        var ordered = _config.Versions
            .Select(v => (Version: v, Key: ParseVersionKey(v.Id)))
            .OrderBy(x => x.Key)
            .ThenBy(x => x.Version.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var changed = false;
        for (var i = 0; i < ordered.Count; i++)
        {
            var desiredOrder = i + 1;
            if (ordered[i].Version.Order != desiredOrder)
            {
                ordered[i].Version.Order = desiredOrder;
                changed = true;
            }
        }

        return changed;
    }

    private readonly record struct VersionKey(int Major, int Minor, int Patch, int BuildDate, string BuildSuffix)
        : IComparable<VersionKey>
    {
        public int CompareTo(VersionKey other)
        {
            var cmp = Major.CompareTo(other.Major);
            if (cmp != 0) return cmp;
            cmp = Minor.CompareTo(other.Minor);
            if (cmp != 0) return cmp;
            cmp = Patch.CompareTo(other.Patch);
            if (cmp != 0) return cmp;
            cmp = BuildDate.CompareTo(other.BuildDate);
            if (cmp != 0) return cmp;
            return StringComparer.OrdinalIgnoreCase.Compare(BuildSuffix, other.BuildSuffix);
        }
    }

    private static VersionKey ParseVersionKey(string id)
    {
        var raw = (id ?? "").Trim();
        if (raw.Length == 0)
            return new VersionKey(0, 0, 0, 0, "");

        var parts = raw.Split('-', 2, StringSplitOptions.RemoveEmptyEntries);
        var basePart = parts[0];
        var buildSuffix = parts.Length > 1 ? parts[1].Trim() : "";

        var nums = basePart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var major = nums.Length > 0 && int.TryParse(nums[0], out var mj) ? mj : 0;
        var minor = nums.Length > 1 && int.TryParse(nums[1], out var mn) ? mn : 0;
        var patch = nums.Length > 2 && int.TryParse(nums[2], out var pt) ? pt : 0;

        var buildDate = 0;
        if (!string.IsNullOrWhiteSpace(buildSuffix))
        {
            buildDate = int.TryParse(buildSuffix, out var dt) ? dt : 99999999;
        }

        return new VersionKey(major, minor, patch, buildDate, buildSuffix);
    }

    private async Task SaveConfigAsync()
    {
        await _configRepository.SaveAsync(_configPath, _config);
    }

    public List<VersionInfo> GetAllVersions() =>
        _config.Versions
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public List<VersionInfo> GetSourceVersions()
    {
        var patchSources = new HashSet<string>(
            _config.Patches.Select(p => NormalizeVersionId(p.From)),
            StringComparer.OrdinalIgnoreCase);

        return _config.Versions
            .Where(v => v.UpgradesTo != null || patchSources.Contains(v.Id))
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<VersionInfo> GetTargetVersions(string fromVersionId)
    {
        var sourceVersion = _config.Versions.FirstOrDefault(v =>
            string.Equals(v.Id, NormalizeVersionId(fromVersionId), StringComparison.OrdinalIgnoreCase));
        if (sourceVersion == null) return new List<VersionInfo>();

        return GetReachableVersions(fromVersionId)
            .Where(v => v.Order > sourceVersion.Order)
            .OrderBy(v => v.Order)
            .ThenBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<PatchStep> CalculateUpgradePath(string fromVersionId, string toVersionId)
    {
        fromVersionId = NormalizeVersionId(fromVersionId);
        toVersionId = NormalizeVersionId(toVersionId);

        if (string.Equals(fromVersionId, toVersionId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("From and To versions cannot be the same.");
        }

        var versionsById = _config.Versions.ToDictionary(v => v.Id, v => v, StringComparer.OrdinalIgnoreCase);
        if (!versionsById.ContainsKey(fromVersionId))
            throw new InvalidOperationException($"Unknown source version '{fromVersionId}'.");
        if (!versionsById.ContainsKey(toVersionId))
            throw new InvalidOperationException($"Unknown target version '{toVersionId}'.");

        var adjacency = _versionGraphService.BuildAdjacencyList(_config.Patches, versionsById);
        if (!adjacency.ContainsKey(fromVersionId))
            throw new InvalidOperationException($"No patches found from source version '{fromVersionId}'.");
        var pathVersions = _versionGraphService.FindShortestPathBySteps(fromVersionId, toVersionId, adjacency);
        if (pathVersions.Count == 0)
            throw new InvalidOperationException($"No upgrade path from {fromVersionId} to {toVersionId}");

        // Convert to PatchSteps (edges)
        var steps = new List<PatchStep>();
        for (var i = 0; i < pathVersions.Count - 1; i++)
        {
            var from = pathVersions[i];
            var to = pathVersions[i + 1];
            var patch = _config.Patches.FirstOrDefault(p =>
                string.Equals(NormalizeVersionId(p.From), from, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeVersionId(p.To), to, StringComparison.OrdinalIgnoreCase));

            if (patch == null)
                throw new InvalidOperationException($"No patch definition found for {from} → {to}.");

            var normalizedScripts = patch.Scripts
                .Select(NormalizeScriptPath)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (normalizedScripts.Count == 0)
                throw new InvalidOperationException($"Patch {from} → {to} has no scripts configured.");

            var absoluteScripts = normalizedScripts.Select(script =>
            {
                var full = ToFullScriptPath(script);
                if (!File.Exists(full))
                    throw new InvalidOperationException($"Missing script file for patch {from} → {to}: {script}");
                return full;
            }).ToList();

            steps.Add(new PatchStep
            {
                FromVersion = from,
                ToVersion = to,
                Scripts = absoluteScripts
            });
        }

        return steps;
    }

    public int GetScriptCount(string versionId)
    {
        // Count scripts in the version's folder
        var versionFolder = Path.Combine(_patchesFolder, versionId);
        if (!Directory.Exists(versionFolder)) return 0;
        return Directory.GetFiles(versionFolder, "*.sql").Length;
    }

    public List<string> GetScriptsForVersion(string versionId)
    {
        // Get scripts directly from the version's folder
        var versionFolder = Path.Combine(_patchesFolder, versionId);
        if (!Directory.Exists(versionFolder)) return new List<string>();
        return Directory.GetFiles(versionFolder, "*.sql")
            .Select(f => Path.GetFileName(f))
            .ToList();
    }

    // ========== VERSION MANAGEMENT ==========

    public async Task AddVersionAsync(string id, string name, string? upgradesTo)
    {
        id = NormalizeVersionId(id);
        name = name.Trim();
        upgradesTo = string.IsNullOrWhiteSpace(upgradesTo) ? null : NormalizeVersionId(upgradesTo);

        // Remove any existing version with same ID
        _config.Versions.RemoveAll(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

        var nextOrder = _config.Versions.Any() ? _config.Versions.Max(v => v.Order) + 1 : 1;
        _config.Versions.Add(new VersionInfo
        {
            Id = id,
            Name = name,
            UpgradesTo = upgradesTo,
            Order = nextOrder
        });

        // Create folder for version
        var versionFolder = Path.Combine(_patchesFolder, id);
        if (!Directory.Exists(versionFolder))
            Directory.CreateDirectory(versionFolder);

        await SaveConfigAsync();
    }

    public async Task UpdateVersionAsync(string id, string name, string? upgradesTo)
    {
        id = NormalizeVersionId(id);
        name = name.Trim();
        upgradesTo = string.IsNullOrWhiteSpace(upgradesTo) ? null : NormalizeVersionId(upgradesTo);

        var version = _config.Versions.FirstOrDefault(v =>
            string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        if (version != null)
        {
            version.Name = name;
            version.UpgradesTo = upgradesTo;
        }
        await SaveConfigAsync();
    }

    public async Task DeleteVersionAsync(string id)
    {
        id = NormalizeVersionId(id);

        _config.Versions.RemoveAll(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
        _config.Patches.RemoveAll(p =>
            string.Equals(NormalizeVersionId(p.From), id, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeVersionId(p.To), id, StringComparison.OrdinalIgnoreCase));

        // Update any versions that pointed to this one
        foreach (var v in _config.Versions.Where(v =>
                     string.Equals(v.UpgradesTo, id, StringComparison.OrdinalIgnoreCase)))
        {
            v.UpgradesTo = null;
        }

        // Delete folder
        var versionFolder = Path.Combine(_patchesFolder, id);
        if (Directory.Exists(versionFolder))
        {
            try
            {
                Directory.Delete(versionFolder, true);
            }
            catch (Exception ex)
            {
                AddNonFatalDiagnostic("DeleteVersionFolder", versionFolder, ex);
            }
        }

        await SaveConfigAsync();
    }

    // ========== SCRIPT MANAGEMENT ==========

    public async Task AddScriptToVersionAsync(string versionId, string scriptSourcePath)
    {
        versionId = NormalizeVersionId(versionId);

        // Copy script to version folder
        var versionFolder = Path.Combine(_patchesFolder, versionId);
        if (!Directory.Exists(versionFolder))
            Directory.CreateDirectory(versionFolder);

        var scriptName = Path.GetFileName(scriptSourcePath);
        var destPath = Path.Combine(versionFolder, scriptName);
        File.Copy(scriptSourcePath, destPath, true);

        // If there's a single "previous" version by upgradesTo chain, attach script to that patch automatically.
        var previousVersion = _config.Versions.FirstOrDefault(v =>
            string.Equals(v.UpgradesTo, versionId, StringComparison.OrdinalIgnoreCase));

        if (previousVersion != null)
        {
            var patch = _config.Patches.FirstOrDefault(p =>
                string.Equals(NormalizeVersionId(p.From), previousVersion.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeVersionId(p.To), versionId, StringComparison.OrdinalIgnoreCase));

            if (patch == null)
            {
                patch = new PatchInfo { From = previousVersion.Id, To = versionId, Scripts = new List<string>() };
                _config.Patches.Add(patch);
            }

            var relativePath = $"{versionId}/{scriptName}";
            if (!patch.Scripts.Any(s =>
                    string.Equals(NormalizeScriptPath(s), relativePath, StringComparison.OrdinalIgnoreCase)))
            {
                patch.Scripts.Add(relativePath);
            }
        }

        await SaveConfigAsync();
    }

    public async Task RemoveScriptFromVersionAsync(string versionId, string scriptName)
    {
        versionId = NormalizeVersionId(versionId);
        scriptName = scriptName.Trim();

        // Remove from patches that target this version
        foreach (var patch in _config.Patches.Where(p =>
                     string.Equals(NormalizeVersionId(p.To), versionId, StringComparison.OrdinalIgnoreCase)))
        {
            var relativePath = $"{versionId}/{scriptName}";
            patch.Scripts.RemoveAll(s =>
                string.Equals(NormalizeScriptPath(s), relativePath, StringComparison.OrdinalIgnoreCase));
            patch.Scripts.RemoveAll(s =>
                string.Equals(Path.GetFileName(s.Replace('/', Path.DirectorySeparatorChar)), scriptName,
                    StringComparison.OrdinalIgnoreCase));
        }

        // Delete file
        var scriptPath = Path.Combine(_patchesFolder, versionId, scriptName);
        if (File.Exists(scriptPath))
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch (Exception ex)
            {
                AddNonFatalDiagnostic("DeleteScriptFile", scriptPath, ex);
            }
        }

        await SaveConfigAsync();
    }

    // ========== PATCH MANAGEMENT ==========

    public List<PatchInfo> GetAllPatches() => _config.Patches;

    public async Task AddPatchAsync(string from, string to)
    {
        from = NormalizeVersionId(from);
        to = NormalizeVersionId(to);

        if (_config.Patches.Any(p =>
                string.Equals(NormalizeVersionId(p.From), from, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeVersionId(p.To), to, StringComparison.OrdinalIgnoreCase)))
            return;

        _config.Patches.Add(new PatchInfo { From = from, To = to, Scripts = new List<string>() });
        await SaveConfigAsync();
    }

    public async Task UpdatePatchAsync(string oldFrom, string oldTo, string newFrom, string newTo)
    {
        oldFrom = NormalizeVersionId(oldFrom);
        oldTo = NormalizeVersionId(oldTo);
        newFrom = NormalizeVersionId(newFrom);
        newTo = NormalizeVersionId(newTo);

        var patch = _config.Patches.FirstOrDefault(p =>
            string.Equals(NormalizeVersionId(p.From), oldFrom, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeVersionId(p.To), oldTo, StringComparison.OrdinalIgnoreCase));
        if (patch != null)
        {
            patch.From = newFrom;
            patch.To = newTo;
        }
        await SaveConfigAsync();
    }

    public async Task DeletePatchAsync(string from, string to)
    {
        from = NormalizeVersionId(from);
        to = NormalizeVersionId(to);
        _config.Patches.RemoveAll(p =>
            string.Equals(NormalizeVersionId(p.From), from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeVersionId(p.To), to, StringComparison.OrdinalIgnoreCase));
        await SaveConfigAsync();
    }

    public async Task UpdatePatchScriptsAsync(string from, string to, List<string> scripts)
    {
        from = NormalizeVersionId(from);
        to = NormalizeVersionId(to);

        var patch = _config.Patches.FirstOrDefault(p =>
            string.Equals(NormalizeVersionId(p.From), from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeVersionId(p.To), to, StringComparison.OrdinalIgnoreCase));
        if (patch != null)
        {
            patch.Scripts = scripts
                .Select(NormalizeScriptPath)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        await SaveConfigAsync();
    }

    public async Task MarkPatchManualAsync(string from, string to)
    {
        from = NormalizeVersionId(from);
        to = NormalizeVersionId(to);

        var patch = _config.Patches.FirstOrDefault(p =>
            string.Equals(NormalizeVersionId(p.From), from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(NormalizeVersionId(p.To), to, StringComparison.OrdinalIgnoreCase));

        if (patch != null && patch.AutoGenerated)
        {
            patch.AutoGenerated = false;
            await SaveConfigAsync();
        }
    }

    public List<string> GetAvailableScripts()
    {
        var scripts = new List<string>();
        if (Directory.Exists(_patchesFolder))
        {
            foreach (var dir in Directory.GetDirectories(_patchesFolder))
            {
                var versionId = Path.GetFileName(dir);
                foreach (var file in Directory.GetFiles(dir, "*.sql"))
                {
                    scripts.Add($"{versionId}/{Path.GetFileName(file)}");
                }
            }
        }
        return scripts;
    }

    public string GetPatchesFolder() => _patchesFolder;

    private static string NormalizeVersionId(string id) => id.Trim();

    private static string NormalizeScriptPath(string path) =>
        path.Trim().Replace('\\', '/');

    private string ToFullScriptPath(string relativePath)
    {
        var normalized = NormalizeScriptPath(relativePath);
        var osPath = normalized.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_patchesFolder, osPath));
        if (!full.StartsWith(_patchesFolderRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Script path escapes patches folder: {relativePath}");
        }

        return full;
    }

    private static string EnsureTrailingSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private void AddNonFatalDiagnostic(string phase, string path, Exception ex)
    {
        _nonFatalDiagnostics.Add(
            $"[{DateTime.UtcNow:O}] {phase}: Path='{path}' {ex.GetType().Name}: {ex.Message}");
    }

    private bool NormalizeConfig()
    {
        var changed = false;

        foreach (var version in _config.Versions)
        {
            var id = NormalizeVersionId(version.Id);
            if (!string.Equals(id, version.Id, StringComparison.Ordinal))
            {
                version.Id = id;
                changed = true;
            }

            var name = version.Name.Trim();
            if (!string.Equals(name, version.Name, StringComparison.Ordinal))
            {
                version.Name = name;
                changed = true;
            }

            var upgradesTo = version.UpgradesTo == null ? null : NormalizeVersionId(version.UpgradesTo);
            if (!string.Equals(upgradesTo, version.UpgradesTo, StringComparison.Ordinal))
            {
                version.UpgradesTo = upgradesTo;
                changed = true;
            }
        }

        foreach (var patch in _config.Patches)
        {
            var from = NormalizeVersionId(patch.From);
            if (!string.Equals(from, patch.From, StringComparison.Ordinal))
            {
                patch.From = from;
                changed = true;
            }

            var to = NormalizeVersionId(patch.To);
            if (!string.Equals(to, patch.To, StringComparison.Ordinal))
            {
                patch.To = to;
                changed = true;
            }

            var normalizedScripts = patch.Scripts
                .Select(NormalizeScriptPath)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            if (!patch.Scripts.SequenceEqual(normalizedScripts))
            {
                patch.Scripts = normalizedScripts;
                changed = true;
            }
        }

        return changed;
    }

    private ConfigValidationResult ValidateConfig()
    {
        var result = new ConfigValidationResult();

        var versionsById = new Dictionary<string, VersionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in _config.Versions)
        {
            if (string.IsNullOrWhiteSpace(v.Id))
            {
                result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, "Version id is empty."));
                continue;
            }

            if (versionsById.ContainsKey(v.Id))
            {
                result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, $"Duplicate version id '{v.Id}'."));
                continue;
            }

            versionsById[v.Id] = v;
        }

        foreach (var group in _config.Versions.GroupBy(v => v.Order))
        {
            if (group.Key <= 0)
            {
                foreach (var v in group)
                    result.Warnings.Add(new ConfigIssue(ConfigIssueSeverity.Warning,
                        $"Version '{v.Id}' has non-positive order '{v.Order}'."));
            }
            else if (group.Count() > 1)
            {
                result.Warnings.Add(new ConfigIssue(
                    ConfigIssueSeverity.Warning,
                    $"Duplicate order '{group.Key}' for versions: {string.Join(", ", group.Select(v => v.Id))}"));
            }
        }

        foreach (var patch in _config.Patches)
        {
            if (string.IsNullOrWhiteSpace(patch.From) || string.IsNullOrWhiteSpace(patch.To))
            {
                result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, "Patch has empty from/to version."));
                continue;
            }

            if (!versionsById.ContainsKey(patch.From))
                result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, $"Patch references missing from version '{patch.From}'."));
            if (!versionsById.ContainsKey(patch.To))
                result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, $"Patch references missing to version '{patch.To}'."));

            if (patch.Scripts.Count == 0)
            {
                result.Warnings.Add(new ConfigIssue(ConfigIssueSeverity.Warning, $"Patch {patch.From} → {patch.To} has no scripts configured."));
            }

            foreach (var script in patch.Scripts.Select(NormalizeScriptPath))
            {
                if (string.IsNullOrWhiteSpace(script))
                    continue;

                string full;
                try
                {
                    full = ToFullScriptPath(script);
                }
                catch (InvalidOperationException ex)
                {
                    result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, ex.Message));
                    continue;
                }

                if (!File.Exists(full))
                {
                    result.Errors.Add(new ConfigIssue(ConfigIssueSeverity.Error, $"Missing script file: {script}"));
                }
            }
        }

        // upgradesTo cycle detection
        foreach (var v in _config.Versions)
        {
            if (string.IsNullOrWhiteSpace(v.UpgradesTo))
                continue;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var current = v.Id;
            while (true)
            {
                if (!seen.Add(current))
                {
                    result.Warnings.Add(new ConfigIssue(ConfigIssueSeverity.Warning,
                        $"Cycle detected in upgradesTo chain starting at '{v.Id}'."));
                    break;
                }

                if (!versionsById.TryGetValue(current, out var currentVersion))
                    break;

                if (string.IsNullOrWhiteSpace(currentVersion.UpgradesTo))
                    break;

                current = currentVersion.UpgradesTo!;
            }
        }

        if (HasPatchCycle(versionsById))
        {
            result.Warnings.Add(new ConfigIssue(ConfigIssueSeverity.Warning, "Cycle detected in patch graph."));
        }

        return result;
    }

    private bool HasPatchCycle(Dictionary<string, VersionInfo> versionsById)
    {
        var adjacency = _config.Patches
            .Where(p => versionsById.ContainsKey(p.From) && versionsById.ContainsKey(p.To))
            .GroupBy(p => p.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.Select(p => p.To).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var color = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unseen 1=visiting 2=done

        bool Dfs(string node)
        {
            if (color.TryGetValue(node, out var c))
            {
                if (c == 1) return true;
                if (c == 2) return false;
            }

            color[node] = 1;
            if (adjacency.TryGetValue(node, out var next))
            {
                foreach (var n in next)
                {
                    if (Dfs(n)) return true;
                }
            }

            color[node] = 2;
            return false;
        }

        foreach (var id in versionsById.Keys)
        {
            if (Dfs(id)) return true;
        }

        return false;
    }

    private List<VersionInfo> GetReachableVersions(string fromVersionId)
    {
        fromVersionId = NormalizeVersionId(fromVersionId);

        var versionsById = _config.Versions.ToDictionary(v => v.Id, v => v, StringComparer.OrdinalIgnoreCase);
        if (!versionsById.TryGetValue(fromVersionId, out var fromVersion))
            return new List<VersionInfo>();

        var adjacency = _versionGraphService.BuildAdjacencyList(_config.Patches, versionsById);
        var visited = _versionGraphService.GetReachableVersions(fromVersionId, adjacency);

        return visited
            .Where(id => !string.Equals(id, fromVersionId, StringComparison.OrdinalIgnoreCase))
            .Select(id => versionsById[id])
            .Where(v => v.Order > fromVersion.Order)
            .ToList();
    }
}
