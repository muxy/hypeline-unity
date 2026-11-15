using System;

namespace MuxyHypeline
{
    /// <summary>
    /// Hypeline Game ID rules and portal links. Create a Game ID for your game at
    /// <see cref="GamesPageUrl"/>; IDs are permanent and cannot be renamed.
    /// </summary>
    public static class HypelineGameId
    {
        /// <summary>Shared ID for trying the samples. Create your own Game ID before release.</summary>
        public const string SharedTestId = "gamelink-testing";
        public const int MinimumLength = 3;
        public const int MaximumLength = 64;

        public const string PortalUrl = "https://dev.muxy.io";
        public const string GamesPageUrl = PortalUrl + "/games";

        public static string PortalUrlForStage(Stage stage)
        {
            return stage == Stage.Staging ? "https://dev.staging.muxy.io" : PortalUrl;
        }

        public static string GamesPageUrlForStage(Stage stage)
        {
            return PortalUrlForStage(stage) + "/games";
        }

        /// <summary>Public lookup for a Game ID; returns game_id, name, status and verified, or 404.</summary>
        public static string PublicLookupUrl(string id)
        {
            return PublicLookupUrl(id, Stage.Production);
        }

        public static string PublicLookupUrl(string id, Stage stage)
        {
            return PortalUrlForStage(stage) + "/api/public/games/" + Uri.EscapeDataString(Normalize(id));
        }

        /// <summary>Trims surrounding whitespace and lowercases. Null becomes an empty string.</summary>
        public static string Normalize(string raw)
        {
            if (raw == null) return "";
            return raw.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// True for 3-64 characters of a-z, 0-9 and single hyphens, starting and ending with
        /// a letter or digit. Expects a normalized ID; uppercase letters are rejected.
        /// </summary>
        public static bool IsValidFormat(string id)
        {
            if (id == null || id.Length < MinimumLength || id.Length > MaximumLength) return false;
            if (id[0] == '-' || id[id.Length - 1] == '-') return false;
            if (id.IndexOf("--", StringComparison.Ordinal) >= 0) return false;
            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool allowed = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!allowed) return false;
            }
            return true;
        }

        /// <summary>True when the normalized ID is the shared test ID.</summary>
        public static bool IsSharedTestId(string id)
        {
            return Normalize(id) == SharedTestId;
        }
    }
}
