﻿using System.Globalization;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DotnetUpdate;

public static class Program
{
    private static readonly UTF8Encoding s_noBom = new(false);
    private static readonly Uri s_versionsUri = new("https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/6.0/releases.json", UriKind.Absolute);
    private static HttpClient s_httpClient;

    public static async Task Main()
    {
        using CancellationTokenSource cts = new(120_000);
        s_httpClient = new HttpClient();
        // string? githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        //
        // if (!string.IsNullOrEmpty(githubToken))
        // {
        //     s_httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
        // }
        Release? r = await GetLatestRelease(cts.Token);
        await CalculateHash(r, cts.Token);
        await WriteDotnetArchives(r.GetBinariesSorted(), "/path/to/dotnet_archives.bzl", cts.Token);
        await PatchContainerStructureTests("/path/to/testdata",
            new Regex(@"\d\.\d\.\d{3}\s*\\+", RegexOptions.Compiled), r.Sdk.Version,
            new Regex(@"\d\.\d\.\d\s*\\+", RegexOptions.Compiled), r.ReleaseVersion,
            cts.Token);
    }

    private static async Task PatchContainerStructureTests(string pathToTestdata, Regex sdkRegex, ReleaseVersion sdkVersion, Regex releaseRegex, ReleaseVersion releaseVersion, CancellationToken cancellationToken)
    {
        static async Task Patch(string file, Regex sdkRegex, ReleaseVersion sdkVersion, Regex releaseRegex, ReleaseVersion releaseVersion, CancellationToken cancellationToken)
        {
            string contents = await File.ReadAllTextAsync(file, cancellationToken);
            string result = sdkRegex.Replace(contents, sdkVersion.ToString());
            result = releaseRegex.Replace(result, releaseVersion.ToString());
            await File.WriteAllTextAsync(file, result, s_noBom, cancellationToken);
        }
        
        string[] files = Directory.GetFiles(pathToTestdata, "*.yaml", SearchOption.TopDirectoryOnly);

        if (files.Length == 0)
        {
            throw new InvalidOperationException("no files found for patching");
        }
        
        List<Task> patching = new(files.Length);
        foreach (string file in files)
        {
            patching.Add(Patch(file, sdkRegex, sdkVersion, releaseRegex, releaseVersion, cancellationToken));
        }

        await Task.WhenAll(patching);
    }

    private static async Task<string> WriteDotnetArchives(DotnetBinaries[] binaries, string archivesPath, CancellationToken cancellationToken)
    {
        string bzl = """
        load("@bazel_tools//tools/build_defs/repo:http.bzl", "http_archive")
            
        // autogenerated using: dotnet run dotnet-utils -c Release --framework net7.0 -- 6.0 6.0.4 6.0.100
        def repositories():
        
        """;

        foreach (DotnetBinaries b in binaries)
        {
            bzl = $"""
            {bzl}
                http_archive(
                    name = "dotnet-6-0_{b.RuntimeName}_{b.Architecture}",
                    build_file = "//experimental/dotnet:BUILD.dotnet",
                    sha256 = "{b.Sha256}",
                    type = "tar.gz",
                    urls = ["{b.Url}"],
                )
            
            """;
        }

        return bzl;
    }

    private static async Task CalculateHash(Release release, CancellationToken cancellationToken)
    {
        try
        {
            await release.CalculateHash(cancellationToken);
        }
        catch(Exception ex)
        {
            throw;
        }
    }

    private static async Task<Release> GetLatestRelease(CancellationToken cancellationToken)
    {
        Versions? v = await s_httpClient.GetFromJsonAsync<Versions>(s_versionsUri, cancellationToken);

        if (v is null)
            throw new InvalidOperationException("Failed to GET releases");

        if (v.LatestRelease == ReleaseVersion.Default
            || v.LatestSdk == ReleaseVersion.Default
            || v.LatestRuntime == ReleaseVersion.Default)
        {
            throw new InvalidOperationException("Undefined latest release");
        }

        if (v.LatestReleaseDate == DateOnly.MinValue
            || v.LatestReleaseDate == DateOnly.MaxValue)
        {
            throw new InvalidOperationException("Undefined latest release data");
        }

        Release? candidate = null;

        // Single
        foreach (Release r in v.Releases)
        {
            if (r.ReleaseDate == v.LatestReleaseDate
                && r.Sdk.Version == v.LatestSdk
                && r.ReleaseVersion == v.LatestRelease
                && r.Runtime.Version == v.LatestRuntime)
            {
                if (candidate is not null)
                {
                    throw new InvalidOperationException("ambiguous latest version");
                }

                candidate = r;
            }
        }

        if (candidate is null)
        {
            throw new InvalidOperationException($"No release found for latest release {v.LatestReleaseDate} {v.LatestSdk} {v.LatestRelease}");
        }

        return candidate;
    }
}

internal sealed  class Versions
{
    public Release[] Releases { get; set; }

    [JsonPropertyName("latest-release")]
    public ReleaseVersion LatestRelease { get; set; }

    [JsonPropertyName("latest-runtime")]
    public ReleaseVersion LatestRuntime { get; set; }

    [JsonPropertyName("latest-sdk")]
    public ReleaseVersion LatestSdk { get; set; }

    [JsonPropertyName("latest-release-date")]
    [JsonConverter(typeof(DateOnlyConverter))]
    public DateOnly LatestReleaseDate { get; set; }
}

internal sealed  class Release
{
    [JsonPropertyName("release-date")]
    [JsonConverter(typeof(DateOnlyConverter))]
    public DateOnly ReleaseDate { get; set; }
    [JsonPropertyName("release-version")]
    public ReleaseVersion ReleaseVersion { get; set; }
    public bool Security { get; set; }
    
    [JsonPropertyName("release-notes")]
    public Uri ReleaseNotes { get; set; }
    public ReleaseDetails Runtime { get; set; }
    public ReleaseDetails Sdk { get; set; }
    [JsonPropertyName("aspnetcore-runtime")]
    public ReleaseDetails AspNetCoreRuntime { get; set; }

    public Task CalculateHash(CancellationToken cancellationToken)
    {
        return Task.WhenAll(
            Sdk.CalculateHash(cancellationToken),
            AspNetCoreRuntime.CalculateHash(cancellationToken),
            Runtime.CalculateHash(cancellationToken)
        );
    }

    public DotnetBinaries[] GetBinariesSorted()
    {
        Sdk.SetName("sdk");
        AspNetCoreRuntime.SetName("aspnetcore");
        Runtime.SetName("runtime");
        return Sdk.Binaries.Concat(AspNetCoreRuntime.Binaries).Concat(Runtime.Binaries).ToArray();
    }
}

internal sealed class ReleaseDetails
{
    public ReleaseVersion Version { get; set; }
    [JsonPropertyName("files")]
    public DotnetBinaries[] Binaries { get; set; }

    public Task CalculateHash(CancellationToken cancellationToken)
    {
        DotnetBinaries amd64 = Binaries.Single(f => f.Rid == "linux-x64");
        amd64.SetArchitecture("amd64");
        DotnetBinaries arm64 = Binaries.Single(f => f.Rid == "linux-arm64");
        arm64.SetArchitecture("arm64");
        Binaries = new [] { amd64, arm64 };

        return Task.WhenAll(amd64.CalculateHash(cancellationToken),
                    arm64.CalculateHash(cancellationToken));
    }

    public void SetName(string name)
    {
        foreach (DotnetBinaries b in Binaries)
        {
            b.SetName(name);
        }
    }
}

internal sealed class DotnetBinaries
{
    public string Name { get; set;  }
    public string Rid { get; set; }
    public Uri Url { get; set; }
    public string Hash { get; set; }
    [JsonIgnore]
    public string Sha256 { get; set; }

    public async Task CalculateHash(CancellationToken ctsToken)
    {
        int retries = 0;
        while (retries < 3 && !ctsToken.IsCancellationRequested)
        {
            try
            {
                using HttpClient client = new HttpClient();
                await using Stream body = await client.GetStreamAsync(Url, ctsToken);
                using var sha512 = SHA512.Create();
                await using CryptoStream sha512Stream = new CryptoStream(body, sha512, CryptoStreamMode.Read);
                using SHA256 sha256 = SHA256.Create();
                await using CryptoStream sha256Stream = new CryptoStream(sha512Stream, sha256, CryptoStreamMode.Read);

                byte[] buffer = new byte[4096 * 4];
                while (await sha256Stream.ReadAsync(buffer, ctsToken) != 0)
                {
                }

                string verifyHash = Convert.ToHexString(sha512.Hash ?? Array.Empty<byte>());
                string bazelHash = Convert.ToHexString(sha256.Hash ?? Array.Empty<byte>());

                if (!string.Equals(Hash, verifyHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new SecurityException($"Hash does not match. File: {Url} Expected: {Hash}, Actual: {verifyHash}");
                }

                Sha256 = bazelHash;
                return;
            }
            catch (Exception ex)
            {
                retries++;
                Console.WriteLine($"{ex.GetType().Name}: {ex.Message}");
            }
        }

    }

    public void SetName(string name)
    {
        RuntimeName = name;
    }

    [JsonIgnore]
    public string RuntimeName { get; private set; } = "undefined";
    [JsonIgnore]
    public string Architecture { get; private set; } = "undefined";
    public void SetArchitecture(string architecture)
    {
        Architecture = architecture;
    }
}

internal sealed class DateOnlyConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {

            string? dateString = reader.GetString();
            if (DateOnly.TryParse(dateString, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly value))
            {
                return value;
            }
            
        }

        return DateOnly.MinValue;
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
    {
        writer.WriteStringValue($"{value.Year}-{value.Month}-{value.Day}");
    }
}

internal sealed class ReleaseVersionConverter : JsonConverter<ReleaseVersion>
{
    public override ReleaseVersion? Read(ref Utf8JsonReader reader, Type _, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return new ReleaseVersion(reader.GetString()!);
        }

        throw new InvalidOperationException($"unexpected token type {reader.TokenType}");
    }

    public override void Write(Utf8JsonWriter writer, ReleaseVersion value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

[JsonConverter(typeof(ReleaseVersionConverter))]
internal sealed class ReleaseVersion : IEquatable<ReleaseVersion>
{
    private static readonly Version DefaultVersion = new(0, 0, 0, 0);
    public static readonly ReleaseVersion Default = new();
    public Version Version { get; } = DefaultVersion;
    public string Suffix { get; } = "";
    private ReleaseVersion(){}
    public ReleaseVersion(string versionString)
    {
        if (Regex.IsMatch(versionString, @"\d+\.\d+\.\d+") && Version.TryParse(versionString, out Version? v))
        {
            Version = v;
        }
        else
        {
           Match match = Regex.Match(versionString, @"^(?<version>\d+\.\d+\.\d+)(?<suffix>-(_|-|\w|\.)+)$");
           if (match.Groups["suffix"].Success && match.Groups["version"].Success)
           {
               if (Version.TryParse(match.Groups["version"].ValueSpan, out v))
               {
                   Version = v;
                   Suffix = match.Groups["suffix"].Value;
               }
           }
        }

    }

    public override string ToString()
    {
        return $"{Version.Major}.{Version.Minor}.{Version.Build}{Suffix}";
    }

    public bool Equals(ReleaseVersion? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Version.Equals(other.Version) && Suffix == other.Suffix;
    }

    public override bool Equals(object? obj)
    {
        return ReferenceEquals(this, obj) || obj is ReleaseVersion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Version, Suffix);
    }

    public static bool operator ==(ReleaseVersion? left, ReleaseVersion? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(ReleaseVersion? left, ReleaseVersion? right)
    {
        return !Equals(left, right);
    }
}