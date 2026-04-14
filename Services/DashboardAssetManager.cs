using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ClashXW.Models;

namespace ClashXW.Services;

public sealed class DashboardAssetManager
{
    private readonly string _bundledAssetsPath;
    private readonly string _installRootPath;
    private readonly string _stateFilePath;
    private readonly string _defaultUpdateUrl;

    public DashboardAssetManager(string bundledAssetsPath, string installRootPath, string stateFilePath, string defaultUpdateUrl)
    {
        _bundledAssetsPath = bundledAssetsPath;
        _installRootPath = installRootPath;
        _stateFilePath = stateFilePath;
        _defaultUpdateUrl = defaultUpdateUrl;
    }

    public DashboardState? LoadState()
    {
        if (!File.Exists(_stateFilePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize<DashboardState>(File.ReadAllText(_stateFilePath));
    }

    public void SaveState(DashboardState state)
    {
        var directory = Path.GetDirectoryName(_stateFilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_stateFilePath, JsonSerializer.Serialize(state));
    }

    public DashboardState EnsureDashboardAssets(string? updateUrl = null)
    {
        Directory.CreateDirectory(_installRootPath);

        var version = ComputeBundledVersion();
        var installDirectory = Path.Combine(_installRootPath, version);
        var resolvedUpdateUrl = string.IsNullOrWhiteSpace(updateUrl) ? _defaultUpdateUrl : updateUrl;

        if (!Directory.Exists(installDirectory) || !File.Exists(Path.Combine(installDirectory, "index.html")))
        {
            CopyDirectory(_bundledAssetsPath, installDirectory);
        }

        var state = new DashboardState(version, installDirectory, resolvedUpdateUrl, null);
        SaveState(state);
        return state;
    }

    private string ComputeBundledVersion()
    {
        if (!Directory.Exists(_bundledAssetsPath))
        {
            return "missing-assets";
        }

        using var sha256 = SHA256.Create();
        foreach (var file in Directory.EnumerateFiles(_bundledAssetsPath, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = Path.GetRelativePath(_bundledAssetsPath, file).Replace('\\', '/');
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relativePath);
            sha256.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            var fileBytes = File.ReadAllBytes(file);
            sha256.TransformBlock(fileBytes, 0, fileBytes.Length, null, 0);
        }

        sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha256.Hash!).ToLowerInvariant()[..12];
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, directory);
            Directory.CreateDirectory(Path.Combine(destinationDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(destinationDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }
}
