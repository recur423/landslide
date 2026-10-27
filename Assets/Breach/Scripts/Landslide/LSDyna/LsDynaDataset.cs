using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

/// <summary>SI, Unity-local-coordinate interchange manifest. It does not certify a solver model.</summary>
[Serializable]
public sealed class LsDynaManifest
{
    public int schemaVersion;
    public string solver;
    public string provenanceKind;
    public string sourceDescription;
    public string sourceSha256;
    public string coordinateConvention;
    public string timeUnit;
    public int[] nodeIds;
    public float[] massesKg;
    public int[] triangles;
    public double[] timesSeconds;
    public string[] frameFiles;
    public string[] frameSha256;
    public string validationSummary;
    public LsDynaSiteCaseReport siteCaseReport;
}

[Serializable]
public sealed class LsDynaSiteCaseReport
{
    public string file;
    public string sha256;
    public string caseId;
    public int evidenceMissingCount;
    public string status;
}

[Serializable]
internal sealed class LsDynaPreparedCase
{
    public int schemaVersion;
    public string status;
    public string caseId;
    public string[] evidenceMissing;
}

/// <summary>One immutable-by-convention exported state, ordered as manifest.nodeIds.</summary>
public sealed class LsDynaFrame
{
    public int Index { get; private set; }
    public Vector3[] Positions { get; private set; }
    public Vector3[] Velocities { get; private set; }

    internal LsDynaFrame(int index, int count)
    {
        Index = index;
        Positions = new Vector3[count];
        Velocities = new Vector3[count];
    }
}

/// <summary>
/// Validates the complete interchange package before playback. All frames are scanned,
/// but at most two decoded frames are cached. Only ordinary local files are supported.
/// Frame hashes check package integrity, not model authenticity. Source hashes only record
/// provenance; the original solver file is not present to authenticate it.
/// </summary>
public sealed class LsDynaDataset
{
    public const int MaximumNodes = 200000;
    public const int MaximumFrames = 100000;
    public const int MaximumTriangleIndices = 3000000;
    public const long MaximumManifestBytes = 32L * 1024 * 1024;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LSDYNA01");
    private readonly string[] framePaths;
    private LsDynaFrame cacheFirst, cacheSecond;
    public LsDynaManifest Manifest { get; private set; }
    public string DirectoryPath { get; private set; }
    public int NodeCount => Manifest.nodeIds.Length;
    public int FrameCount => Manifest.timesSeconds.Length;
    public double StartTime => Manifest.timesSeconds[0];
    public double EndTime => Manifest.timesSeconds[FrameCount - 1];
    public double Duration => EndTime - StartTime;
    public float MaximumSourceSpeed { get; private set; }

    private LsDynaDataset(string directory, LsDynaManifest manifest, string[] paths)
    {
        DirectoryPath = directory;
        Manifest = manifest;
        framePaths = paths;
    }

    public static LsDynaDataset Load(string directory, bool allowAnalytic = false)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new InvalidDataException("A local LS-DYNA dataset directory is required.");
        // Android StreamingAssets is a jar URL, not a System.IO directory.
        if (directory.Contains("://") || directory.StartsWith("jar:", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("LS-DYNA playback requires an extracted local directory. Android jar/StreamingAssets and web URLs are not supported.");
        string fullDirectory = Path.GetFullPath(directory);
        if (!Directory.Exists(fullDirectory)) throw new DirectoryNotFoundException("LS-DYNA dataset directory is missing: " + fullDirectory);
        RejectReparsePoint(fullDirectory);
        string manifestPath = ResolveContainedPath(fullDirectory, "dataset.json");
        var info = new FileInfo(manifestPath);
        if (!info.Exists) throw new FileNotFoundException("LS-DYNA dataset.json is missing.", manifestPath);
        if (info.Length == 0 || info.Length > MaximumManifestBytes) throw new InvalidDataException("LS-DYNA manifest is empty or exceeds the 32 MiB limit.");
        LsDynaManifest manifest;
        try { manifest = JsonUtility.FromJson<LsDynaManifest>(File.ReadAllText(manifestPath, new UTF8Encoding(false, true))); }
        catch (Exception error) when (error is ArgumentException || error is DecoderFallbackException)
        { throw new InvalidDataException("Invalid UTF-8 LS-DYNA JSON manifest.", error); }
        ValidateManifest(manifest, allowAnalytic);
        ValidateSiteCaseReport(fullDirectory, manifest.siteCaseReport);
        var paths = new string[manifest.frameFiles.Length];
        var uniquePaths = new HashSet<string>(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        for (int i = 0; i < paths.Length; i++)
        {
            paths[i] = ResolveContainedPath(fullDirectory, manifest.frameFiles[i]);
            if (!uniquePaths.Add(paths[i])) throw new InvalidDataException("Each LS-DYNA time state must have its own frame file.");
        }
        var dataset = new LsDynaDataset(fullDirectory, manifest, paths);
        // This is deliberately a streaming scan, not an all-frame allocation.
        for (int i = 0; i < paths.Length; i++) dataset.ReadAndValidateFrame(i, false);
        return dataset;
    }

    private static void ValidateManifest(LsDynaManifest m, bool allowAnalytic)
    {
        if (m == null || m.schemaVersion != 1) throw new InvalidDataException("Expected LS-DYNA interchange schemaVersion 1.");
        bool solverExport = m.solver == "LS-DYNA" && m.provenanceKind == "solver-export";
        bool analytic = m.solver == "ANALYTIC" && m.provenanceKind == "analytic-verification";
        if (!solverExport && !(allowAnalytic && analytic))
            throw new InvalidDataException("A genuine LS-DYNA solver-export package is required. ANALYTIC verification data must be explicitly enabled and is not solver output.");
        if (m.coordinateConvention != "Unity-local-m" || m.timeUnit != "s")
            throw new InvalidDataException("Convert coordinates and units first: expected Unity-local-m and seconds (s).");
        if (string.IsNullOrWhiteSpace(m.sourceDescription) || string.IsNullOrWhiteSpace(m.validationSummary))
            throw new InvalidDataException("Source description and validation summary are required.");
        ValidateSha256(m.sourceSha256, "sourceSha256");
        if (m.nodeIds == null || m.nodeIds.Length == 0 || m.nodeIds.Length > MaximumNodes)
            throw new InvalidDataException("Node count must be between 1 and " + MaximumNodes + ".");
        if (m.massesKg == null || m.massesKg.Length != m.nodeIds.Length)
            throw new InvalidDataException("massesKg must have one mass for every node ID.");
        var ids = new HashSet<int>();
        for (int i = 0; i < m.nodeIds.Length; i++)
        {
            if (m.nodeIds[i] <= 0 || !ids.Add(m.nodeIds[i])) throw new InvalidDataException("Node IDs must be unique positive integers.");
            if (!Finite(m.massesKg[i]) || m.massesKg[i] <= 0f) throw new InvalidDataException("Nodal masses must be finite positive kilograms.");
        }
        if (m.triangles == null || m.triangles.Length > MaximumTriangleIndices || m.triangles.Length % 3 != 0)
            throw new InvalidDataException("triangles must be an explicit array of zero-based triangle indices, or an empty array.");
        for (int i = 0; i < m.triangles.Length; i += 3)
        {
            int a = m.triangles[i], b = m.triangles[i + 1], c = m.triangles[i + 2];
            if (a < 0 || b < 0 || c < 0 || a >= m.nodeIds.Length || b >= m.nodeIds.Length || c >= m.nodeIds.Length || a == b || b == c || c == a)
                throw new InvalidDataException("Triangle indices must reference three distinct existing nodes.");
        }
        if (m.timesSeconds == null || m.timesSeconds.Length == 0 || m.timesSeconds.Length > MaximumFrames ||
            m.frameFiles == null || m.frameFiles.Length != m.timesSeconds.Length)
            throw new InvalidDataException("Provide matching nonempty timesSeconds and frameFiles arrays (maximum " + MaximumFrames + " states).");
        if (m.frameSha256 == null || m.frameSha256.Length != m.frameFiles.Length)
            throw new InvalidDataException("Schema 1 requires one frameSha256 checksum for every frame file. Re-export the package with the converter.");
        for (int i = 0; i < m.frameSha256.Length; i++) ValidateSha256(m.frameSha256[i], "frameSha256[" + i + "]");
        for (int i = 0; i < m.timesSeconds.Length; i++)
            if (!Finite(m.timesSeconds[i]) || (i > 0 && m.timesSeconds[i] <= m.timesSeconds[i - 1]))
                throw new InvalidDataException("Solver times must be finite and strictly increasing.");
        if (!Finite(m.timesSeconds[m.timesSeconds.Length - 1] - m.timesSeconds[0]))
            throw new InvalidDataException("Solver time range is too large.");
    }

    private static void ValidateSiteCaseReport(string directory, LsDynaSiteCaseReport reference)
    {
        if (reference == null) return;
        if (reference.file != "site_case_report.json" || reference.status != "prepared-not-solved" ||
            string.IsNullOrWhiteSpace(reference.caseId) || reference.evidenceMissingCount < 0)
            throw new InvalidDataException("Site case reference must describe a prepared, unsolved case report.");
        ValidateSha256(reference.sha256, "siteCaseReport.sha256");
        string path = ResolveContainedPath(directory, reference.file);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0 || info.Length > MaximumManifestBytes)
            throw new InvalidDataException("Referenced site case report is missing, empty, or too large.");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha = SHA256.Create())
        {
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            if (!string.Equals(actual, reference.sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Site case report SHA-256 integrity mismatch.");
        }
        LsDynaPreparedCase report;
        try { report = JsonUtility.FromJson<LsDynaPreparedCase>(File.ReadAllText(path, new UTF8Encoding(false, true))); }
        catch (Exception error) when (error is ArgumentException || error is DecoderFallbackException)
        { throw new InvalidDataException("Invalid site case report JSON.", error); }
        if (report == null || report.schemaVersion != 1 || report.status != reference.status ||
            report.caseId != reference.caseId || report.evidenceMissing == null ||
            report.evidenceMissing.Length != reference.evidenceMissingCount)
            throw new InvalidDataException("Site case report does not match its dataset reference.");
    }

    private static void ValidateSha256(string value, string field)
    {
        if (value == null || value.Length != 64)
            throw new InvalidDataException(field + " must contain 64 hexadecimal characters.");
        foreach (char ch in value)
            if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F')))
                throw new InvalidDataException(field + " is not a hexadecimal SHA-256 checksum.");
    }

    private static string ResolveContainedPath(string directory, string relative)
    {
        RejectReparsePoint(directory);
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(":") || relative.IndexOf('\0') >= 0)
            throw new InvalidDataException("Frame paths must be relative local paths within the dataset.");
        // Reject traversal even where normalization would happen to stay inside the folder.
        string normalized = relative.Replace('\\', '/');
        foreach (string segment in normalized.Split('/'))
            if (segment.Length == 0 || segment == "." || segment == ".." || segment.TrimEnd(' ', '.') != segment)
                throw new InvalidDataException("Empty, traversal, and trailing-space/dot path segments are forbidden.");
        string root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison)) throw new InvalidDataException("Frame path escapes the dataset directory.");
        string current = directory;
        foreach (string segment in normalized.Split('/'))
        {
            current = Path.Combine(current, segment);
            RejectReparsePoint(current);
        }
        return full;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Dataset symbolic links and junctions are forbidden: " + path);
    }

    public LsDynaFrame ReadFrame(int index)
    {
        if (index < 0 || index >= FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (cacheFirst != null && cacheFirst.Index == index) return cacheFirst;
        if (cacheSecond != null && cacheSecond.Index == index)
        {
            var found = cacheSecond; cacheSecond = cacheFirst; cacheFirst = found;
            return found;
        }
        var frame = ReadAndValidateFrame(index, true);
        cacheSecond = cacheFirst;
        cacheFirst = frame;
        return frame;
    }

    private LsDynaFrame ReadAndValidateFrame(int index, bool decode)
    {
        string path = framePaths[index];
        // Recheck containment/reparse points on each access, including after load.
        ResolveContainedPath(DirectoryPath, Manifest.frameFiles[index]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream))
        {
            long expected = 12L + NodeCount * 24L;
            if (stream.Length != expected) throw new InvalidDataException("Incorrect LS-DYNA binary frame length: " + path);
            // Hash the same open read-only handle that will be decoded. ComputeHash streams
            // through a small buffer; it does not retain another binary frame in memory.
            using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
                if (!string.Equals(actual, Manifest.frameSha256[index], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("LS-DYNA frame " + index + " SHA-256 integrity mismatch. The file differs from this manifest; re-export the package. Checksums do not authenticate the solver model: " + path);
            }
            stream.Position = 0;
            for (int j = 0; j < Magic.Length; j++)
                if (reader.ReadByte() != Magic[j]) throw new InvalidDataException("Expected LSDYNA01 frame signature: " + path);
            if (reader.ReadInt32() != NodeCount) throw new InvalidDataException("Binary node count does not match manifest: " + path);
            var result = decode ? new LsDynaFrame(index, NodeCount) : null;
            for (int i = 0; i < NodeCount; i++)
            {
                float x = reader.ReadSingle(), y = reader.ReadSingle(), z = reader.ReadSingle();
                float vx = reader.ReadSingle(), vy = reader.ReadSingle(), vz = reader.ReadSingle();
                if (!Finite(x) || !Finite(y) || !Finite(z) || !Finite(vx) || !Finite(vy) || !Finite(vz))
                    throw new InvalidDataException("Non-finite position/velocity in frame " + index + ", node " + Manifest.nodeIds[i] + ".");
                double speed = Math.Sqrt((double)vx * vx + (double)vy * vy + (double)vz * vz);
                if (speed > float.MaxValue) throw new InvalidDataException("Velocity magnitude exceeds Unity float precision.");
                MaximumSourceSpeed = Math.Max(MaximumSourceSpeed, (float)speed);
                if (decode) { result.Positions[i] = new Vector3(x, y, z); result.Velocities[i] = new Vector3(vx, vy, vz); }
            }
            return result;
        }
    }

    public int FindLowerFrame(double absoluteSimulationSeconds)
    {
        if (!Finite(absoluteSimulationSeconds)) throw new ArgumentOutOfRangeException(nameof(absoluteSimulationSeconds));
        double[] times = Manifest.timesSeconds;
        int low = 0, high = times.Length - 1;
        if (absoluteSimulationSeconds <= times[0]) return 0;
        if (absoluteSimulationSeconds >= times[high]) return high;
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            if (times[middle] <= absoluteSimulationSeconds) low = middle; else high = middle;
        }
        return low;
    }

    internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
