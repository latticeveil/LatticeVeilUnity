using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace LatticeVeil.Launcher
{
    /// <summary>
    /// Checks the latest GitHub release for the LatticeVeil Unity build.
    /// Compares the release tag to Application.version and exposes
    /// the release name for display in the launcher subtitle.
    ///
    /// Release convention:
    ///   tag_name : "v1.0.0" (compared case-insensitively to Application.version)
    ///   name     : "V1.0.0 - Veilwalkers: Unified" (full display string)
    ///
    /// Dev builds skip the remote check and show only the local version.
    /// </summary>
    internal sealed class VersionChecker
    {
        private const string ReleasesUrl =
            "https://api.github.com/repos/latticeveil/LatticeVeilUnity/releases/latest";
        private const string UserAgent = "LatticeVeil-Launcher/1.0";

        public enum CheckState
        {
            NotStarted,
            Checking,
            UpToDate,
            OutOfDate,
            NoRelease,
            Unavailable
        }

        public CheckState State { get; private set; } = CheckState.NotStarted;
        public string RemoteDisplayName { get; private set; } = string.Empty;
        public string RemoteTag { get; private set; } = string.Empty;
        public string LocalVersion { get; private set; } = string.Empty;

        public async Task CheckAsync(CancellationToken ct = default)
        {
            LocalVersion = Application.version;
            State = CheckState.Checking;

            if (Core.Paths.IsDevBuild)
            {
                RemoteDisplayName = string.Empty;
                State = CheckState.UpToDate;
                return;
            }

            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                http.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                var response = await http.GetAsync(ReleasesUrl, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    State = CheckState.NoRelease;
                    return;
                }

                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();

                var tag  = ExtractJsonString(json, "tag_name");
                var name = ExtractJsonString(json, "name");

                if (string.IsNullOrWhiteSpace(tag))
                {
                    State = CheckState.Unavailable;
                    return;
                }

                RemoteTag         = tag;
                RemoteDisplayName = string.IsNullOrWhiteSpace(name) ? tag : name;

                var normalizedTag   = tag.TrimStart('v', 'V').Trim();
                var normalizedLocal = LocalVersion.Trim();

                State = string.Equals(normalizedTag, normalizedLocal, StringComparison.OrdinalIgnoreCase)
                    ? CheckState.UpToDate
                    : CheckState.OutOfDate;
            }
            catch (OperationCanceledException)
            {
                State = CheckState.Unavailable;
            }
            catch
            {
                State = CheckState.Unavailable;
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            var search = "\"" + key + "\"";
            var idx = json.IndexOf(search, StringComparison.Ordinal);
            if (idx < 0) return null;

            var colonIdx = json.IndexOf(':', idx + search.Length);
            if (colonIdx < 0) return null;

            var quoteOpen = json.IndexOf('"', colonIdx + 1);
            if (quoteOpen < 0) return null;

            var quoteClose = quoteOpen + 1;
            while (quoteClose < json.Length)
            {
                if (json[quoteClose] == '\\') { quoteClose += 2; continue; }
                if (json[quoteClose] == '"')   break;
                quoteClose++;
            }

            if (quoteClose >= json.Length) return null;
            return json.Substring(quoteOpen + 1, quoteClose - quoteOpen - 1);
        }
    }
}