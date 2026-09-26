using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace SmartSaver.Services;

public enum CloudAuthStatus
{
    NotLoggedIn,
    Loading,
    PendingApproval,
    DeviceMismatch,
    Banned,
    Expired,
    Approved,
    UpdateRequired
}

public class CloudUserAccount
{
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public bool IsApproved { get; set; }
    public string Status { get; set; } = "pending"; // "approved", "pending", "banned"
    public string Role { get; set; } = "user"; // "admin", "user"
    public bool IsAdmin { get; set; }
    public long ExpiryTimestamp { get; set; }
    public long RegistrationTimestamp { get; set; }
    public long LastActiveTimestamp { get; set; }
    public long LastOnlineVerifiedTimestamp { get; set; }
    public string? IntegritySignature { get; set; }
    public string AppTag { get; set; } = "dasmo_pc_suite";
    public string AppVersion { get; set; } = "1.5.0";

    // Silent Rich PC Specs
    public string CpuModel { get; set; } = string.Empty;
    public string RamTotal { get; set; } = string.Empty;
    public string OsBuild { get; set; } = string.Empty;
    public string ScreenRes { get; set; } = string.Empty;
    public string WindowsUser { get; set; } = string.Empty;
    public string Motherboard { get; set; } = string.Empty;
    public string LocalIp { get; set; } = string.Empty;
}

public class AppVersionPolicy
{
    public string LatestVersion { get; set; } = "1.5.0";
    public string MinRequiredVersion { get; set; } = "1.5.0";
    public string BlockedVersions { get; set; } = string.Empty;
    public bool ForceUpdate { get; set; } = false;
    public string CustomUpdateMessage { get; set; } = string.Empty;
    public string UpdateDownloadUrl { get; set; } = string.Empty;
    public string GithubRepo { get; set; } = "SUBHOJITPAUL797/DASMO-CYBER-CAFE-TOOLS";
    public string UpdateChangelog { get; set; } = string.Empty;
    public long LastUpdatedTimestamp { get; set; }
}

/// <summary>
/// Enterprise Cloud Authentication & Physical Hardware Device Lock Service.
/// Communicates directly with Google Firestore via official REST endpoints without heavy external dependencies.
/// </summary>
public class FirebaseCloudAuthService
{
    private static readonly Lazy<FirebaseCloudAuthService> _instance = new(() => new FirebaseCloudAuthService());
    public static FirebaseCloudAuthService Instance => _instance.Value;

    public const string ProjectId = "dasmo-scanner-android";
    public const string ApiKey = "AIzaSyDjVqas7AANFiZLpVUUuxqBXAPwRIdAQzM";
    public const string SuperAdminEmail = "subhojitpaul26042004@gmail.com";
    public const string PcUsersCollection = "dasmo_pc_users";
    private const string AdminSecretKey = "DASMO_ADMIN_SEC_26042004_SUBHOJIT_CYBER_KEY";

    public static bool BypassForTests { get; set; } = false;
    public string? LastErrorMessage { get; private set; }

    private readonly HttpClient _http;
    private readonly string _sessionFilePath;
    public string SessionFilePath => _sessionFilePath;
    private readonly string _gateFilePath;
    private CancellationTokenSource? _pollCts;

    public CloudAuthStatus CurrentStatus { get; private set; } = CloudAuthStatus.NotLoggedIn;
    public CloudUserAccount? CurrentUser { get; private set; }
    public bool IsSuperAdmin => CurrentUser?.Email.Equals(SuperAdminEmail, StringComparison.OrdinalIgnoreCase) == true;
    public static string CurrentAppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.5.0";
    public AppVersionPolicy? CurrentVersionPolicy { get; private set; }

    public event Action<CloudAuthStatus, CloudUserAccount?>? OnAuthStateChanged;

    private FirebaseCloudAuthService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string newDir = Path.Combine(appData, "DASMO CYBER CAFE TOOLS");
        Directory.CreateDirectory(newDir);

        _sessionFilePath = Path.Combine(newDir, "pc_auth_session.json");
        _gateFilePath = Path.Combine(newDir, "licensing_gate.json");

        // Backward compatibility: migrate from old SmartSaver directory if present
        try
        {
            string oldSession = Path.Combine(appData, "SmartSaver", "pc_auth_session.json");
            string oldGate = Path.Combine(appData, "SmartSaver", "licensing_gate.json");
            if (!File.Exists(_sessionFilePath) && File.Exists(oldSession))
            {
                File.Copy(oldSession, _sessionFilePath, overwrite: true);
            }
            if (!File.Exists(_gateFilePath) && File.Exists(oldGate))
            {
                File.Copy(oldGate, _gateFilePath, overwrite: true);
            }
        }
        catch { }
    }

    private const string IntegritySalt = "DASMO_CYBER_SECURE_TOKEN_2026_V1_F9B8C7D6";

    private class EncryptedSessionEnvelope
    {
        public int Version { get; set; } = 2;
        public string Hwid { get; set; } = string.Empty;
        public string Iv { get; set; } = string.Empty;
        public string Ciphertext { get; set; } = string.Empty;
        public string Mac { get; set; } = string.Empty;
    }

    private static string ComputeSessionSignature(CloudUserAccount user, string hwid)
    {
        string payload = $"{user.Email?.Trim().ToLowerInvariant()}|{hwid}|{user.Status}|{user.IsApproved}|{user.Role}|{user.IsAdmin}|{user.ExpiryTimestamp}|{user.RegistrationTimestamp}|{user.AppVersion}";
        byte[] keyBytes = Encoding.UTF8.GetBytes($"{hwid}_{IntegritySalt}");
        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    private static string ComputeSessionSignatureLegacy(CloudUserAccount user, string hwid)
    {
        string payload = $"{user.Email?.Trim().ToLowerInvariant()}|{hwid}|{user.Status}|{user.IsApproved}|{user.Role}|{user.IsAdmin}|{user.ExpiryTimestamp}|{user.RegistrationTimestamp}";
        byte[] keyBytes = Encoding.UTF8.GetBytes($"{hwid}_{IntegritySalt}");
        using var hmac = new HMACSHA256(keyBytes);
        byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Loads local session cache on app startup and checks status against hardware ID.
    /// Tamper-proof: verifies cryptographic HMAC-SHA256 signature, machine hardware lock, and AES envelope.
    /// </summary>
    public async Task<CloudAuthStatus> InitializeAndCheckAuthAsync()
    {
        try
        {
            if (!File.Exists(_sessionFilePath))
            {
                CurrentUser = null;
                CurrentStatus = CloudAuthStatus.NotLoggedIn;
                return CurrentStatus;
            }

            string fileContent = await File.ReadAllTextAsync(_sessionFilePath).ConfigureAwait(false);
            string currentHwid = HardwareIdService.GetHardwareId();
            CloudUserAccount? cached = null;

            // Check if file is encrypted envelope v2
            if (fileContent.Contains("\"Version\": 2") || fileContent.Contains("\"v\": 2") || fileContent.Contains("\"Ciphertext\""))
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<EncryptedSessionEnvelope>(fileContent);
                    if (envelope != null && !string.IsNullOrEmpty(envelope.Ciphertext) && !string.IsNullOrEmpty(envelope.Iv))
                    {
                        // Verify physical hardware lock
                        if (!string.Equals(envelope.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Warning("SECURITY ALERT: Session envelope hardware mismatch! Stored: {StoredHwid}, Current: {CurrentHwid}. Deleting session.", envelope.Hwid, currentHwid);
                            File.Delete(_sessionFilePath);
                            CurrentUser = null;
                            CurrentStatus = CloudAuthStatus.NotLoggedIn;
                            return CurrentStatus;
                        }

                        // Verify HMAC integrity
                        using var sha = SHA256.Create();
                        byte[] hmacKey = sha.ComputeHash(Encoding.UTF8.GetBytes(currentHwid + "_HMAC_" + IntegritySalt));
                        using var hmac = new HMACSHA256(hmacKey);
                        byte[] expectedMac = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{envelope.Iv}:{envelope.Ciphertext}"));
                        if (!string.Equals(envelope.Mac, Convert.ToHexString(expectedMac), StringComparison.OrdinalIgnoreCase))
                        {
                            Log.Warning("SECURITY ALERT: Tampered session envelope detected! MAC mismatch. Deleting session.");
                            File.Delete(_sessionFilePath);
                            CurrentUser = null;
                            CurrentStatus = CloudAuthStatus.NotLoggedIn;
                            return CurrentStatus;
                        }

                        // Decrypt ciphertext
                        byte[] aesKey = sha.ComputeHash(Encoding.UTF8.GetBytes(currentHwid + "_AES_" + IntegritySalt));
                        byte[] iv = Convert.FromBase64String(envelope.Iv);
                        byte[] cipherBytes = Convert.FromBase64String(envelope.Ciphertext);

                        using var aes = Aes.Create();
                        aes.Key = aesKey;
                        aes.IV = iv;
                        using var ms = new MemoryStream();
                        using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                        {
                            await cs.WriteAsync(cipherBytes, 0, cipherBytes.Length);
                            await cs.FlushFinalBlockAsync();
                        }
                        string decryptedJson = Encoding.UTF8.GetString(ms.ToArray());
                        cached = JsonSerializer.Deserialize<CloudUserAccount>(decryptedJson);

                        // Verify internal payload signature
                        if (cached != null)
                        {
                            string expectedSig = ComputeSessionSignature(cached, currentHwid);
                            string legacySig = ComputeSessionSignatureLegacy(cached, currentHwid);
                            if (!string.Equals(cached.IntegritySignature, expectedSig, StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(cached.IntegritySignature, legacySig, StringComparison.OrdinalIgnoreCase))
                            {
                                Log.Warning("SECURITY ALERT: Inner session signature mismatch! Tampered user account fields detected. Deleting session.");
                                File.Delete(_sessionFilePath);
                                CurrentUser = null;
                                CurrentStatus = CloudAuthStatus.NotLoggedIn;
                                return CurrentStatus;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to decrypt session envelope. Resetting session.");
                    try { File.Delete(_sessionFilePath); } catch { }
                    CurrentUser = null;
                    CurrentStatus = CloudAuthStatus.NotLoggedIn;
                    return CurrentStatus;
                }
            }
            else
            {
                // Legacy v1 plain JSON migration
                try
                {
                    var legacy = JsonSerializer.Deserialize<CloudUserAccount>(fileContent);
                    if (legacy != null && string.Equals(legacy.Email, SuperAdminEmail, StringComparison.OrdinalIgnoreCase))
                    {
                        // Safely auto-upgrade Super Admin to encrypted v2
                        cached = legacy;
                        await SaveSessionCacheAsync(cached).ConfigureAwait(false);
                        Log.Information("Upgraded Super Admin session to tamper-proof encrypted store v2.");
                    }
                    else
                    {
                        // Non-admin legacy session must be re-verified online
                        Log.Information("Legacy session format detected for non-admin. Triggering online verification.");
                    }
                }
                catch { }
            }

            if (cached == null || string.IsNullOrWhiteSpace(cached.Email))
            {
                CurrentUser = null;
                CurrentStatus = CloudAuthStatus.NotLoggedIn;
                return CurrentStatus;
            }

            bool isSuperAdmin = string.Equals(cached.Email, SuperAdminEmail, StringComparison.OrdinalIgnoreCase);
            bool isApprovedCached = cached.IsApproved ||
                string.Equals(cached.Status, "approved", StringComparison.OrdinalIgnoreCase) ||
                isSuperAdmin;

            bool isDeviceMatch = string.IsNullOrEmpty(cached.DeviceId) || 
                string.Equals(cached.DeviceId, currentHwid, StringComparison.OrdinalIgnoreCase);

            if (!isDeviceMatch)
            {
                Log.Warning("Cached session bound to device {BoundId} but running on {CurrentHwid}", cached.DeviceId, currentHwid);
                CurrentUser = null;
                CurrentStatus = CloudAuthStatus.DeviceMismatch;
                return CurrentStatus;
            }

            // Check offline grace limit: 7 days max for non-admin
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool clockRolledBack = !isSuperAdmin && 
                cached.LastOnlineVerifiedTimestamp > 0 && 
                (nowMs < cached.LastOnlineVerifiedTimestamp - 3600_000L); // 1-hour tolerance for time zone drift

            bool offlineGraceExpired = !isSuperAdmin && 
                cached.LastOnlineVerifiedTimestamp > 0 && 
                (nowMs - cached.LastOnlineVerifiedTimestamp > 7L * 24 * 60 * 60 * 1000);

            if (clockRolledBack)
            {
                Log.Warning("SECURITY ALERT: System clock rollback detected! Last verified: {Last}, Current: {Now}", cached.LastOnlineVerifiedTimestamp, nowMs);
                CurrentUser = null;
                CurrentStatus = CloudAuthStatus.NotLoggedIn;
                return CurrentStatus;
            }

            if (isApprovedCached && !offlineGraceExpired)
            {
                CurrentUser = cached;
                CurrentStatus = CloudAuthStatus.Approved;

                // Fire non-blocking background online sync & timestamp update (only when not running automated unit tests)
                if (!BypassForTests)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await LoginAsync(cached.Email, null, isNewRegistration: false).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log.Debug(ex, "Background cloud auth check fallback to cache.");
                        }
                    });
                }

                return CurrentStatus;
            }

            // If not approved or offline grace expired, verify online with 5s timeout
            if (BypassForTests)
            {
                CurrentUser = null;
                CurrentStatus = CloudAuthStatus.NotLoggedIn;
                return CurrentStatus;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            return await Task.Run(() => LoginAsync(cached.Email, null, isNewRegistration: false), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to restore cached PC auth session. Defaulting to NotLoggedIn.");
            CurrentStatus = CloudAuthStatus.NotLoggedIn;
            return CurrentStatus;
        }
    }

    /// <summary>
    /// Authenticates a user email, binds the physical PC hardware ID, captures rich specs silently, and checks admin approval.
    /// </summary>
    public async Task<CloudAuthStatus> LoginAsync(string email, string? name = null, bool isNewRegistration = false)
    {
        string normalized = email.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized))
        {
            CurrentStatus = CloudAuthStatus.NotLoggedIn;
            OnAuthStateChanged?.Invoke(CurrentStatus, null);
            return CurrentStatus;
        }

        // Only set status to Loading and notify UI if not already Approved (prevents background sync from disrupting UI)
        if (CurrentStatus != CloudAuthStatus.Approved)
        {
            CurrentStatus = CloudAuthStatus.Loading;
            OnAuthStateChanged?.Invoke(CurrentStatus, null);
        }

        var specs = HardwareIdService.GetComputerSpecs();
        string currentHwId = specs.DeviceId;
        string currentModel = HardwareIdService.GetDeviceModel();
        bool isSuperAdmin = normalized.Equals(SuperAdminEmail, StringComparison.OrdinalIgnoreCase);

        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{PcUsersCollection}/{Uri.EscapeDataString(normalized)}?key={ApiKey}";
            var response = await _http.GetAsync(url).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                // Document exists in Firestore
                string respJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var user = ParseFirestoreUserDocument(respJson);

                // Hardware binding check (1 Account = 1 Physical PC — Enforced on EVERY account including Admin)
                if (string.IsNullOrEmpty(user.DeviceId))
                {
                    // First login (or pre-approved user, or unbound by admin) -> Bind this physical PC and sync specs silently
                    user.DeviceId = currentHwId;
                    user.DeviceModel = currentModel;
                    if (!string.IsNullOrWhiteSpace(name))
                        user.Name = name.Trim();
                    user.CpuModel = specs.Processor;
                    user.RamTotal = specs.RamTotal;
                    user.OsBuild = specs.OsVersion;
                    user.ScreenRes = specs.ScreenResolution;
                    user.WindowsUser = specs.WindowsUser;
                    user.Motherboard = specs.Motherboard;
                    user.LocalIp = specs.LocalIp;
                    user.LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    user.AppVersion = CurrentAppVersion;
                    var updates = new Dictionary<string, object>
                    {
                        ["deviceId"] = currentHwId,
                        ["deviceModel"] = currentModel,
                        ["appVersion"] = CurrentAppVersion,
                        ["cpuModel"] = specs.Processor,
                        ["ramTotal"] = specs.RamTotal,
                        ["osBuild"] = specs.OsVersion,
                        ["screenRes"] = specs.ScreenResolution,
                        ["windowsUser"] = specs.WindowsUser,
                        ["motherboard"] = specs.Motherboard,
                        ["localIp"] = specs.LocalIp,
                        ["lastActiveTimestamp"] = user.LastActiveTimestamp
                    };
                    if (!string.IsNullOrEmpty(user.Name))
                        updates["name"] = user.Name;

                    await UpdateUserFieldsAsync(PcUsersCollection, normalized, updates).ConfigureAwait(false);
                }
                else if (!string.Equals(user.DeviceId, currentHwId, StringComparison.OrdinalIgnoreCase))
                {
                    // Attempting to run account on an unauthorized second PC
                    Log.Warning("DEVICE MISMATCH: Account {Email} is bound to {BoundHwId}, but running on {CurrentHwId}", normalized, user.DeviceId, currentHwId);
                    CurrentUser = user;
                    CurrentStatus = CloudAuthStatus.DeviceMismatch;
                    OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                    return CurrentStatus;
                }
                else
                {
                    // Existing bound PC: update specs and appVersion
                    user.AppVersion = CurrentAppVersion;
                    var updates = new Dictionary<string, object>
                    {
                        ["appVersion"] = CurrentAppVersion,
                        ["lastActiveTimestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    if (!string.IsNullOrEmpty(user.Name)) updates["name"] = user.Name;
                    if (string.IsNullOrEmpty(user.CpuModel) || (!string.IsNullOrWhiteSpace(name) && string.IsNullOrEmpty(user.Name)))
                    {
                        if (!string.IsNullOrWhiteSpace(name)) user.Name = name.Trim();
                        user.CpuModel = specs.Processor;
                        user.RamTotal = specs.RamTotal;
                        user.OsBuild = specs.OsVersion;
                        user.ScreenRes = specs.ScreenResolution;
                        user.WindowsUser = specs.WindowsUser;
                        user.Motherboard = specs.Motherboard;
                        user.LocalIp = specs.LocalIp;

                        updates["cpuModel"] = specs.Processor;
                        updates["ramTotal"] = specs.RamTotal;
                        updates["osBuild"] = specs.OsVersion;
                        updates["screenRes"] = specs.ScreenResolution;
                        updates["windowsUser"] = specs.WindowsUser;
                        updates["motherboard"] = specs.Motherboard;
                        updates["localIp"] = specs.LocalIp;
                    }
                    _ = UpdateUserFieldsAsync(PcUsersCollection, normalized, updates);
                }

                // Check Ban status
                if (string.Equals(user.Status, "banned", StringComparison.OrdinalIgnoreCase))
                {
                    user.IsApproved = false;
                    CurrentUser = user;
                    CurrentStatus = CloudAuthStatus.Banned;
                    await SaveSessionCacheAsync(user).ConfigureAwait(false);
                    OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                    return CurrentStatus;
                }

                // Check Expiration
                if (user.ExpiryTimestamp > 0 && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > user.ExpiryTimestamp)
                {
                    user.IsApproved = false;
                    CurrentUser = user;
                    CurrentStatus = CloudAuthStatus.Expired;
                    await SaveSessionCacheAsync(user).ConfigureAwait(false);
                    OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                    return CurrentStatus;
                }

                // Check Approval
                bool approved = isSuperAdmin || user.IsApproved || string.Equals(user.Status, "approved", StringComparison.OrdinalIgnoreCase);
                if (!approved)
                {
                    user.IsApproved = false;
                    CurrentUser = user;
                    CurrentStatus = CloudAuthStatus.PendingApproval;
                    await SaveSessionCacheAsync(user).ConfigureAwait(false);
                    OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                    return CurrentStatus;
                }

                // Check Version Enforcement Policy
                if (!isSuperAdmin && !BypassForTests)
                {
                    bool versionAllowed = await CheckVersionPolicyEnforcementAsync().ConfigureAwait(false);
                    if (!versionAllowed)
                    {
                        return CurrentStatus; // UpdateRequired
                    }
                }

                // Full Approval Verified!
                user.IsApproved = true;
                user.Status = "approved";
                user.LastOnlineVerifiedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (isSuperAdmin)
                {
                    user.Role = "admin";
                    user.IsAdmin = true;
                }

                CurrentUser = user;
                CurrentStatus = CloudAuthStatus.Approved;
                await SaveSessionCacheAsync(user).ConfigureAwait(false);

                // Update last active in background
                _ = UpdateUserFieldsAsync(PcUsersCollection, normalized, new Dictionary<string, object>
                {
                    ["lastActiveTimestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });

                OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                return CurrentStatus;
            }
            else if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                if (!isNewRegistration)
                {
                    // Account was deleted by Super Admin from Firebase!
                    Log.Warning("Account {Email} not found in Firestore (deleted by admin or unlinked). Wiping session.", normalized);
                    Logout();
                    return CloudAuthStatus.NotLoggedIn;
                }

                // New registration -> check if open registration mode (approval requirement removed) is active
                bool requireApproval = true;
                try
                {
                    requireApproval = await IsApprovalRequiredForPcAsync().ConfigureAwait(false);
                }
                catch { }

                bool autoApprove = isSuperAdmin || !requireApproval;

                var newUser = new CloudUserAccount
                {
                    Email = normalized,
                    Name = string.IsNullOrWhiteSpace(name) ? (isSuperAdmin ? "Subhojit Paul (Super Admin)" : "Desktop User") : name.Trim(),
                    DeviceId = currentHwId,
                    DeviceModel = currentModel,
                    IsApproved = autoApprove,
                    Status = autoApprove ? "approved" : "pending",
                    Role = isSuperAdmin ? "admin" : "user",
                    IsAdmin = isSuperAdmin,
                    ExpiryTimestamp = 0,
                    RegistrationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    LastOnlineVerifiedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    AppTag = "dasmo_pc_suite",
                    CpuModel = specs.Processor,
                    RamTotal = specs.RamTotal,
                    OsBuild = specs.OsVersion,
                    ScreenRes = specs.ScreenResolution,
                    WindowsUser = specs.WindowsUser,
                    Motherboard = specs.Motherboard,
                    LocalIp = specs.LocalIp,
                    AppVersion = CurrentAppVersion
                };

                await CreateFirestoreUserDocumentAsync(PcUsersCollection, newUser).ConfigureAwait(false);
                CurrentUser = newUser;

                if (autoApprove && !isSuperAdmin && !BypassForTests)
                {
                    bool versionAllowed = await CheckVersionPolicyEnforcementAsync().ConfigureAwait(false);
                    if (!versionAllowed)
                    {
                        return CurrentStatus; // UpdateRequired
                    }
                }

                CurrentStatus = autoApprove ? CloudAuthStatus.Approved : CloudAuthStatus.PendingApproval;
                await SaveSessionCacheAsync(newUser).ConfigureAwait(false);

                OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                return CurrentStatus;
            }
            else
            {
                string err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                Log.Error("Firestore Login Error {Code}: {Error}", response.StatusCode, err);
                LastErrorMessage = $"Cloud License Error ({response.StatusCode}): {err}";

                // Server temporary error (5xx) -> maintain approved session if valid offline
                if ((int)response.StatusCode >= 500 && CurrentUser != null && (CurrentUser.IsApproved || isSuperAdmin))
                {
                    Log.Warning("Firestore 5xx server error. Maintaining approved offline session.");
                    CurrentStatus = CloudAuthStatus.Approved;
                    return CurrentStatus;
                }

                CurrentStatus = CloudAuthStatus.NotLoggedIn;
                OnAuthStateChanged?.Invoke(CurrentStatus, null);
                return CurrentStatus;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception connecting to Firestore for user: {Email}", normalized);
            LastErrorMessage = $"Network/Connection Error: {ex.Message}";

            // Offline Grace Protection: If user is already approved and offline grace has not expired, PRESERVE Approved status!
            if (CurrentUser != null && (CurrentUser.IsApproved || string.Equals(CurrentUser.Status, "approved", StringComparison.OrdinalIgnoreCase) || isSuperAdmin))
            {
                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                bool offlineGraceExpired = !isSuperAdmin && 
                    CurrentUser.LastOnlineVerifiedTimestamp > 0 && 
                    (nowMs - CurrentUser.LastOnlineVerifiedTimestamp > 7L * 24 * 60 * 60 * 1000);

                if (!offlineGraceExpired)
                {
                    Log.Information("Offline mode active: Preserving approved session for {Email} despite network drop.", normalized);
                    CurrentStatus = CloudAuthStatus.Approved;
                    return CurrentStatus;
                }
            }

            CurrentStatus = CloudAuthStatus.NotLoggedIn;
            OnAuthStateChanged?.Invoke(CurrentStatus, null);
            return CurrentStatus;
        }
    }

    /// <summary>
    /// Starts background polling to detect when the Super Admin approves this PC user.
    /// </summary>
    public void StartPollingForApproval(string email, Action<CloudAuthStatus> onResult)
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var token = _pollCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3500, token);
                    if (token.IsCancellationRequested) break;

                    var status = await LoginAsync(email, null, isNewRegistration: false);
                    if (status == CloudAuthStatus.Approved || status == CloudAuthStatus.Banned || status == CloudAuthStatus.DeviceMismatch || status == CloudAuthStatus.NotLoggedIn)
                    {
                        onResult(status);
                        break;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Error during approval poll");
                }
            }
        }, token);
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        _pollCts?.Dispose();
        _pollCts = null;
    }

    private System.Threading.Timer? _heartbeatTimer;

    public void StartLicenseHeartbeat()
    {
        _heartbeatTimer?.Dispose();
        // Check license every 3 minutes in background
        _heartbeatTimer = new System.Threading.Timer(async _ =>
        {
            if (CurrentUser != null && !string.IsNullOrEmpty(CurrentUser.Email))
            {
                try
                {
                    await LoginAsync(CurrentUser.Email, null, isNewRegistration: false).ConfigureAwait(false);
                }
                catch { }
            }
        }, null, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3));
    }

    public void Logout()
    {
        StopPolling();
        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;
        CurrentUser = null;
        CurrentStatus = CloudAuthStatus.NotLoggedIn;
        try
        {
            if (File.Exists(_sessionFilePath))
                File.Delete(_sessionFilePath);
        }
        catch { }
        OnAuthStateChanged?.Invoke(CurrentStatus, null);
    }

    // ─────────────────────────────────────────────────────────────
    // 🛡️ ADMIN MANAGEMENT METHODS (Subhojit Paul Only)
    // ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Retrieves all users from the specified collection.
    /// </summary>
    public async Task<List<CloudUserAccount>> GetAllUsersAsync(string collectionName = PcUsersCollection)
    {
        var list = new List<CloudUserAccount>();
        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{collectionName}?key={ApiKey}&pageSize=100";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return list;

            string json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("documents", out var docsArray) && docsArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in docsArray.EnumerateArray())
                {
                    list.Add(ParseFirestoreUserDocument(d.GetRawText()));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to list users from {Collection}", collectionName);
        }
        return list;
    }

    public async Task<bool> ApproveUserAsync(string collectionName, string email)
    {
        return await UpdateUserFieldsAsync(collectionName, email, new Dictionary<string, object>
        {
            ["isApproved"] = true,
            ["status"] = "approved"
        });
    }

    public async Task<bool> BanUserAsync(string collectionName, string email)
    {
        return await UpdateUserFieldsAsync(collectionName, email, new Dictionary<string, object>
        {
            ["isApproved"] = false,
            ["status"] = "banned"
        });
    }

    public async Task<bool> SetPendingUserAsync(string collectionName, string email)
    {
        return await UpdateUserFieldsAsync(collectionName, email, new Dictionary<string, object>
        {
            ["isApproved"] = false,
            ["status"] = "pending"
        });
    }

    public async Task<bool> UnbindDeviceAsync(string collectionName, string email)
    {
        return await UpdateUserFieldsAsync(collectionName, email, new Dictionary<string, object>
        {
            ["deviceId"] = "",
            ["deviceModel"] = ""
        });
    }

    public async Task<bool> SetExpiryAsync(string collectionName, string email, long expiryTimestamp)
    {
        return await UpdateUserFieldsAsync(collectionName, email, new Dictionary<string, object>
        {
            ["expiryTimestamp"] = expiryTimestamp
        });
    }

    public async Task<bool> DeleteUserAsync(string collectionName, string email)
    {
        try
        {
            if (IsSuperAdmin)
            {
                // First flag as deleted with admin key so Firestore rule permits deletion
                await UpdateUserFieldsAsync(collectionName, email, new Dictionary<string, object>
                {
                    ["status"] = "deleted"
                });
            }

            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{collectionName}/{Uri.EscapeDataString(email.Trim().ToLowerInvariant())}?key={ApiKey}";
            var resp = await _http.DeleteAsync(url);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete user {Email} in {Collection}", email, collectionName);
            return false;
        }
    }

    public async Task<bool> PreApproveUserAsync(string collectionName, string email, string name, string role = "user")
    {
        try
        {
            string normalized = email.Trim().ToLowerInvariant();
            var user = new CloudUserAccount
            {
                Email = normalized,
                Name = string.IsNullOrWhiteSpace(name) ? "Pre-Approved User" : name.Trim(),
                DeviceId = "", // Unbound: binds physical PC on first run
                DeviceModel = "Awaiting First Login",
                IsApproved = true,
                Status = "approved",
                Role = role,
                IsAdmin = role.Equals("admin", StringComparison.OrdinalIgnoreCase),
                ExpiryTimestamp = 0,
                RegistrationTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                LastActiveTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                AppTag = collectionName
            };

            return await CreateFirestoreUserDocumentAsync(collectionName, user).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to pre-approve user {Email}", email);
            return false;
        }
    }

    public bool GetLocalGateCache()
    {
        try
        {
            if (File.Exists(_gateFilePath))
            {
                string json = File.ReadAllText(_gateFilePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("requireApprovalForPc", out var prop))
                    return prop.GetBoolean();
            }
        }
        catch { }
        return false; // Default to false (Open Mode) once set
    }

    public void SaveLocalGateCache(bool requireApproval)
    {
        try
        {
            string dir = Path.GetDirectoryName(_gateFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(new { requireApprovalForPc = requireApproval }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_gateFilePath, json);
        }
        catch { }
    }

    public async Task<bool> IsApprovalRequiredForPcAsync()
    {
        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/system_config/licensing?key={ApiKey}";
            var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fields", out var fields))
                {
                    bool required = GetBoolField(fields, "requireApprovalForPc");
                    SaveLocalGateCache(required);
                    return required;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch licensing config from Firestore. Defaulting to local cache.");
        }
        return GetLocalGateCache();
    }

    public async Task<bool> SetApprovalRequiredForPcAsync(bool requireApproval)
    {
        SaveLocalGateCache(requireApproval);
        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/system_config/licensing?updateMask.fieldPaths=requireApprovalForPc&updateMask.fieldPaths=lastUpdatedBy&updateMask.fieldPaths=lastUpdatedTimestamp&updateMask.fieldPaths=adminKey&key={ApiKey}";
            var payload = new
            {
                fields = new Dictionary<string, object>
                {
                    ["requireApprovalForPc"] = new { booleanValue = requireApproval },
                    ["lastUpdatedBy"] = new { stringValue = "Subhojit Paul" },
                    ["lastUpdatedTimestamp"] = new { integerValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() },
                    ["adminKey"] = new { stringValue = AdminSecretKey }
                }
            };
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update licensing settings in Firestore");
            return false;
        }
    }

    public async Task<AppVersionPolicy> GetVersionPolicyAsync()
    {
        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/system_config/licensing?key={ApiKey}";
            var resp = await _http.GetAsync(url).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fields", out var fields))
                {
                    var policy = new AppVersionPolicy
                    {
                        LatestVersion = GetStringField(fields, "latestVersion"),
                        MinRequiredVersion = GetStringField(fields, "minRequiredVersion"),
                        BlockedVersions = GetStringField(fields, "blockedVersions"),
                        ForceUpdate = GetBoolField(fields, "forceUpdate"),
                        CustomUpdateMessage = GetStringField(fields, "customUpdateMessage"),
                        UpdateDownloadUrl = GetStringField(fields, "updateDownloadUrl"),
                        GithubRepo = GetStringField(fields, "githubRepo"),
                        UpdateChangelog = GetStringField(fields, "updateChangelog"),
                        LastUpdatedTimestamp = GetLongField(fields, "lastUpdatedTimestamp")
                    };
                    if (string.IsNullOrWhiteSpace(policy.LatestVersion)) policy.LatestVersion = CurrentAppVersion;
                    if (string.IsNullOrWhiteSpace(policy.MinRequiredVersion)) policy.MinRequiredVersion = CurrentAppVersion;
                    if (string.IsNullOrWhiteSpace(policy.GithubRepo)) policy.GithubRepo = AppUpdateService.DefaultGithubRepo;
                    CurrentVersionPolicy = policy;
                    return policy;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch version policy from Firestore");
        }

        return CurrentVersionPolicy ?? new AppVersionPolicy { LatestVersion = CurrentAppVersion, MinRequiredVersion = CurrentAppVersion };
    }

    public async Task<bool> SetVersionPolicyAsync(AppVersionPolicy policy)
    {
        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/system_config/licensing?updateMask.fieldPaths=latestVersion&updateMask.fieldPaths=minRequiredVersion&updateMask.fieldPaths=blockedVersions&updateMask.fieldPaths=forceUpdate&updateMask.fieldPaths=customUpdateMessage&updateMask.fieldPaths=updateDownloadUrl&updateMask.fieldPaths=githubRepo&updateMask.fieldPaths=updateChangelog&updateMask.fieldPaths=lastUpdatedBy&updateMask.fieldPaths=lastUpdatedTimestamp&updateMask.fieldPaths=adminKey&key={ApiKey}";
            var payload = new
            {
                fields = new Dictionary<string, object>
                {
                    ["latestVersion"] = new { stringValue = policy.LatestVersion ?? CurrentAppVersion },
                    ["minRequiredVersion"] = new { stringValue = policy.MinRequiredVersion ?? CurrentAppVersion },
                    ["blockedVersions"] = new { stringValue = policy.BlockedVersions ?? "" },
                    ["forceUpdate"] = new { booleanValue = policy.ForceUpdate },
                    ["customUpdateMessage"] = new { stringValue = policy.CustomUpdateMessage ?? "" },
                    ["updateDownloadUrl"] = new { stringValue = policy.UpdateDownloadUrl ?? "" },
                    ["githubRepo"] = new { stringValue = string.IsNullOrWhiteSpace(policy.GithubRepo) ? AppUpdateService.DefaultGithubRepo : policy.GithubRepo.Trim() },
                    ["updateChangelog"] = new { stringValue = policy.UpdateChangelog ?? "" },
                    ["lastUpdatedBy"] = new { stringValue = "Subhojit Paul" },
                    ["lastUpdatedTimestamp"] = new { integerValue = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() },
                    ["adminKey"] = new { stringValue = AdminSecretKey }
                }
            };
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(HttpMethod.Patch, url) { Content = content };
            var resp = await _http.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                CurrentVersionPolicy = policy;
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save version policy in Firestore");
            return false;
        }
    }

    public static bool IsVersionOutdated(string currentVersion, string minRequiredVersion)
    {
        if (string.IsNullOrWhiteSpace(minRequiredVersion)) return false;
        if (Version.TryParse(NormalizeVersion(currentVersion), out var current) && 
            Version.TryParse(NormalizeVersion(minRequiredVersion), out var min))
        {
            return current < min;
        }
        return false;
    }

    public static bool IsVersionBlocked(string currentVersion, string blockedVersionsList)
    {
        if (string.IsNullOrWhiteSpace(blockedVersionsList)) return false;
        string normCurrent = NormalizeVersion(currentVersion);
        var parts = blockedVersionsList.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Any(b => string.Equals(NormalizeVersion(b), normCurrent, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "0.0.0";
        v = v.Trim().TrimStart('v', 'V');
        var segments = v.Split('.');
        if (segments.Length == 1) return $"{v}.0.0";
        if (segments.Length == 2) return $"{v}.0";
        return v;
    }

    public async Task<bool> CheckVersionPolicyEnforcementAsync()
    {
        if (BypassForTests || IsSuperAdmin) return true;

        try
        {
            var policy = await GetVersionPolicyAsync().ConfigureAwait(false);
            CurrentVersionPolicy = policy;
            if (policy.ForceUpdate)
            {
                if (IsVersionOutdated(CurrentAppVersion, policy.MinRequiredVersion) ||
                    IsVersionBlocked(CurrentAppVersion, policy.BlockedVersions))
                {
                    Log.Warning("MANDATORY UPDATE REQUIRED: Current {Cur} is below minimum {Min} or blocked", CurrentAppVersion, policy.MinRequiredVersion);
                    CurrentStatus = CloudAuthStatus.UpdateRequired;
                    OnAuthStateChanged?.Invoke(CurrentStatus, CurrentUser);
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to check version policy");
        }
        return true;
    }

    // ─────────────────────────────────────────────────────────────
    // HELPERS & FIRESTORE SERIALIZERS
    // ─────────────────────────────────────────────────────────────

    private async Task<bool> CreateFirestoreUserDocumentAsync(string collection, CloudUserAccount user)
    {
        try
        {
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{collection}?documentId={Uri.EscapeDataString(user.Email)}&key={ApiKey}";
            var fields = new Dictionary<string, object>
            {
                ["email"] = new { stringValue = user.Email },
                ["name"] = new { stringValue = user.Name },
                ["deviceId"] = new { stringValue = user.DeviceId },
                ["deviceModel"] = new { stringValue = user.DeviceModel },
                ["isApproved"] = new { booleanValue = user.IsApproved },
                ["status"] = new { stringValue = user.Status },
                ["role"] = new { stringValue = user.Role },
                ["isAdmin"] = new { booleanValue = user.IsAdmin },
                ["expiryTimestamp"] = new { integerValue = user.ExpiryTimestamp.ToString() },
                ["registrationTimestamp"] = new { integerValue = user.RegistrationTimestamp.ToString() },
                ["lastActiveTimestamp"] = new { integerValue = user.LastActiveTimestamp.ToString() },
                ["appTag"] = new { stringValue = user.AppTag },
                ["cpuModel"] = new { stringValue = user.CpuModel },
                ["ramTotal"] = new { stringValue = user.RamTotal },
                ["osBuild"] = new { stringValue = user.OsBuild },
                ["screenRes"] = new { stringValue = user.ScreenRes },
                ["windowsUser"] = new { stringValue = user.WindowsUser },
                ["motherboard"] = new { stringValue = user.Motherboard },
                ["localIp"] = new { stringValue = user.LocalIp },
                ["appVersion"] = new { stringValue = string.IsNullOrEmpty(user.AppVersion) ? CurrentAppVersion : user.AppVersion }
            };

            if (IsSuperAdmin || CurrentUser?.IsAdmin == true || user.Email.Equals(SuperAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                fields["adminKey"] = new { stringValue = AdminSecretKey };
            }

            var payload = new { fields };
            string json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(url, content);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create user doc for {Email}", user.Email);
            return false;
        }
    }

    private async Task<bool> UpdateUserFieldsAsync(string collection, string email, Dictionary<string, object> updates)
    {
        try
        {
            if (IsSuperAdmin || CurrentUser?.IsAdmin == true || email.Trim().Equals(SuperAdminEmail, StringComparison.OrdinalIgnoreCase))
            {
                updates["adminKey"] = AdminSecretKey;
            }

            var updateMasks = new List<string>();
            var fieldsObj = new Dictionary<string, object>();

            foreach (var kvp in updates)
            {
                updateMasks.Add($"updateMask.fieldPaths={kvp.Key}");
                if (kvp.Value is bool bVal)
                    fieldsObj[kvp.Key] = new { booleanValue = bVal };
                else if (kvp.Value is long lVal)
                    fieldsObj[kvp.Key] = new { integerValue = lVal.ToString() };
                else if (kvp.Value is int iVal)
                    fieldsObj[kvp.Key] = new { integerValue = iVal.ToString() };
                else
                    fieldsObj[kvp.Key] = new { stringValue = kvp.Value?.ToString() ?? string.Empty };
            }

            string maskParams = string.Join("&", updateMasks);
            string url = $"https://firestore.googleapis.com/v1/projects/{ProjectId}/databases/(default)/documents/{collection}/{Uri.EscapeDataString(email.Trim().ToLowerInvariant())}?{maskParams}&key={ApiKey}";

            string json = JsonSerializer.Serialize(new { fields = fieldsObj });
            using var req = new HttpRequestMessage(HttpMethod.Patch, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var resp = await _http.SendAsync(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to update fields for {Email}", email);
            return false;
        }
    }

    private CloudUserAccount ParseFirestoreUserDocument(string json)
    {
        var user = new CloudUserAccount();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("fields", out var fields))
            {
                user.Email = GetStringField(fields, "email");
                user.Name = GetStringField(fields, "name", "fullName", "userName");
                user.DeviceId = GetStringField(fields, "deviceId", "dasmo_deviceId");
                user.DeviceModel = GetStringField(fields, "deviceModel");
                user.IsApproved = GetBoolField(fields, "isApproved", "dasmo_isApproved");
                user.Status = GetStringField(fields, "status", "dasmo_status");
                if (string.IsNullOrEmpty(user.Status)) user.Status = user.IsApproved ? "approved" : "pending";
                user.Role = GetStringField(fields, "role", "dasmo_role");
                user.IsAdmin = GetBoolField(fields, "isAdmin", "dasmo_isAdmin");
                user.ExpiryTimestamp = GetLongField(fields, "expiryTimestamp");
                user.RegistrationTimestamp = GetLongField(fields, "registrationTimestamp");
                user.LastActiveTimestamp = GetLongField(fields, "lastActiveTimestamp");
                user.AppTag = GetStringField(fields, "appTag");
                user.AppVersion = GetStringField(fields, "appVersion");
                if (string.IsNullOrEmpty(user.AppVersion)) user.AppVersion = "1.0.0";

                // Silent Specs
                user.CpuModel = GetStringField(fields, "cpuModel");
                user.RamTotal = GetStringField(fields, "ramTotal");
                user.OsBuild = GetStringField(fields, "osBuild");
                user.ScreenRes = GetStringField(fields, "screenRes");
                user.WindowsUser = GetStringField(fields, "windowsUser");
                user.Motherboard = GetStringField(fields, "motherboard");
                user.LocalIp = GetStringField(fields, "localIp");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to parse Firestore user JSON");
        }
        return user;
    }

    private static string GetStringField(JsonElement fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetProperty(name, out var prop) && prop.TryGetProperty("stringValue", out var val))
                return val.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static bool GetBoolField(JsonElement fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetProperty(name, out var prop) && prop.TryGetProperty("booleanValue", out var val))
                return val.GetBoolean();
        }
        return false;
    }

    private static long GetLongField(JsonElement fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetProperty(name, out var prop) && prop.TryGetProperty("integerValue", out var val))
            {
                if (long.TryParse(val.GetString(), out long num)) return num;
            }
        }
        return 0L;
    }

    private async Task SaveSessionCacheAsync(CloudUserAccount user)
    {
        try
        {
            string dir = Path.GetDirectoryName(_sessionFilePath)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string currentHwid = HardwareIdService.GetHardwareId();
            if (user.LastOnlineVerifiedTimestamp <= 0)
                user.LastOnlineVerifiedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            user.IntegritySignature = ComputeSessionSignature(user, currentHwid);

            string json = JsonSerializer.Serialize(user, new JsonSerializerOptions { WriteIndented = false });
            byte[] plainBytes = Encoding.UTF8.GetBytes(json);

            // Derive 256-bit AES key & 256-bit HMAC key from hardware ID and internal salt
            using var sha = SHA256.Create();
            byte[] aesKey = sha.ComputeHash(Encoding.UTF8.GetBytes(currentHwid + "_AES_" + IntegritySalt));
            byte[] hmacKey = sha.ComputeHash(Encoding.UTF8.GetBytes(currentHwid + "_HMAC_" + IntegritySalt));

            byte[] iv = new byte[16];
            RandomNumberGenerator.Fill(iv);

            byte[] cipherBytes;
            using (var aes = Aes.Create())
            {
                aes.Key = aesKey;
                aes.IV = iv;
                using var ms = new MemoryStream();
                using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    await cs.WriteAsync(plainBytes, 0, plainBytes.Length);
                    await cs.FlushFinalBlockAsync();
                }
                cipherBytes = ms.ToArray();
            }

            string ivB64 = Convert.ToBase64String(iv);
            string cipherB64 = Convert.ToBase64String(cipherBytes);

            // Compute HMAC over IV + Ciphertext
            byte[] macBytes;
            using (var hmac = new HMACSHA256(hmacKey))
            {
                byte[] macPayload = Encoding.UTF8.GetBytes($"{ivB64}:{cipherB64}");
                macBytes = hmac.ComputeHash(macPayload);
            }

            var envelope = new EncryptedSessionEnvelope
            {
                Version = 2,
                Hwid = currentHwid,
                Iv = ivB64,
                Ciphertext = cipherB64,
                Mac = Convert.ToHexString(macBytes)
            };

            string envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_sessionFilePath, envelopeJson);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to save encrypted session cache");
        }
    }
}
