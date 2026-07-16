using System.Net.Http;
using Nexus.Service.Cloud;
using Nexus.Service.Models.Cloud;

namespace Nexus.Service.Tests.Cloud;

/// <summary>
/// In-memory ICloudApiClient double. Each method delegates to a public Func
/// field defaulting to a network error, so a test only wires up the calls it
/// actually exercises. Call counters let ordering/retry tests assert how many
/// times each endpoint was hit. Public (not internal) so Integration/ tests
/// can register it in NexusAppFactory's DI container as a swapped-in
/// ICloudApiClient.
/// </summary>
public sealed class FakeCloudApiClient : ICloudApiClient
{
    public int RegisterCalls;
    public int LoginCalls;
    public int RefreshCalls;
    public int LogoutCalls;
    public int RecoveryStartCalls;
    public int RecoveryPollCalls;
    public int ChangePasswordCalls;
    public int ChangeUsernameCalls;
    public int SetPrivateCalls;
    public int DeleteAccountCalls;
    public int UploadAvatarCalls;
    public int PutDeviceCalls;
    public int ListProfilesCalls;
    public int GetProfileCalls;
    public int PutProfileCalls;
    public int DeleteProfileCalls;
    public int PostRawCalls;
    public int SendRawCalls;

    public Func<CloudRegisterRequest, CloudApiResult<CloudVoid>> OnRegister = _ => CloudApiResult<CloudVoid>.NetworkError("not wired");
    public Func<CloudLoginRequest, CloudApiResult<CloudAuthSession>> OnLogin = _ => CloudApiResult<CloudAuthSession>.NetworkError("not wired");
    public Func<string, CloudApiResult<CloudAuthSession>> OnRefresh = _ => CloudApiResult<CloudAuthSession>.NetworkError("not wired");
    public Func<string, CloudApiResult<CloudVoid>> OnLogout = _ => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
    public Func<CloudRecoveryStartRequest, CloudApiResult<CloudVoid>> OnRecoveryStart = _ => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
    public Func<CloudRecoveryPollRequest, CloudApiResult<CloudRecoveryPollResponse>> OnRecoveryPoll =
        _ => CloudApiResult<CloudRecoveryPollResponse>.Ok(new CloudRecoveryPollResponse { Status = "expired" });
    public Func<string, CloudChangePasswordRequest, CloudApiResult<CloudVoid>> OnChangePassword = (_, _) => CloudApiResult<CloudVoid>.NetworkError("not wired");
    public Func<string, CloudChangeUsernameRequest, CloudApiResult<CloudVoid>> OnChangeUsername = (_, _) => CloudApiResult<CloudVoid>.NetworkError("not wired");
    public Func<string, bool, CloudApiResult<CloudVoid>> OnSetPrivate = (_, _) => CloudApiResult<CloudVoid>.NetworkError("not wired");
    public Func<string, string?, CloudApiResult<CloudVoid>> OnDeleteAccount = (_, _) => CloudApiResult<CloudVoid>.NetworkError("not wired");
    public Func<string, byte[], string, CloudApiResult<CloudAvatarUploadResponse>> OnUploadAvatar = (_, _, _) => CloudApiResult<CloudAvatarUploadResponse>.NetworkError("not wired");
    public Func<string, string, CloudDevicePutRequest, CloudApiResult<CloudVoid>> OnPutDevice = (_, _, _) => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
    public Func<string, CloudApiResult<List<CloudProfileSummaryDto>>> OnListProfiles = _ => CloudApiResult<List<CloudProfileSummaryDto>>.Ok(new List<CloudProfileSummaryDto>());
    public Func<string, string, CloudApiResult<CloudProfileDto>> OnGetProfile = (_, _) => CloudApiResult<CloudProfileDto>.NetworkError("not wired");
    public Func<string, string, CloudPutProfileRequest, CloudApiResult<CloudPutProfileResult>> OnPutProfile = (_, _, _) => CloudApiResult<CloudPutProfileResult>.NetworkError("not wired");
    public Func<string, string, CloudApiResult<CloudVoid>> OnDeleteProfile = (_, _) => CloudApiResult<CloudVoid>.Ok(CloudVoid.Instance);
    public Func<string, string, string?, CloudApiResult<CloudRawResponse>> OnPostRaw =
        (_, body, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = body });
    public Func<HttpMethod, string, string?, string?, CloudApiResult<CloudRawResponse>> OnSendRaw =
        (_, _, body, _) => CloudApiResult<CloudRawResponse>.Ok(new CloudRawResponse { Body = body ?? "" });

    public Task<CloudApiResult<CloudVoid>> RegisterAsync(CloudRegisterRequest body, CancellationToken ct)
    { RegisterCalls++; return Task.FromResult(OnRegister(body)); }

    public Task<CloudApiResult<CloudAuthSession>> LoginAsync(CloudLoginRequest body, CancellationToken ct)
    { LoginCalls++; return Task.FromResult(OnLogin(body)); }

    public Task<CloudApiResult<CloudAuthSession>> RefreshAsync(string refreshToken, CancellationToken ct)
    { RefreshCalls++; return Task.FromResult(OnRefresh(refreshToken)); }

    public Task<CloudApiResult<CloudVoid>> LogoutAsync(string refreshToken, CancellationToken ct)
    { LogoutCalls++; return Task.FromResult(OnLogout(refreshToken)); }

    public Task<CloudApiResult<CloudVoid>> RecoveryStartAsync(CloudRecoveryStartRequest body, CancellationToken ct)
    { RecoveryStartCalls++; return Task.FromResult(OnRecoveryStart(body)); }

    public Task<CloudApiResult<CloudRecoveryPollResponse>> RecoveryPollAsync(CloudRecoveryPollRequest body, CancellationToken ct)
    { RecoveryPollCalls++; return Task.FromResult(OnRecoveryPoll(body)); }

    public Task<CloudApiResult<CloudVoid>> ChangePasswordAsync(string accessToken, CloudChangePasswordRequest body, CancellationToken ct)
    { ChangePasswordCalls++; return Task.FromResult(OnChangePassword(accessToken, body)); }

    public Task<CloudApiResult<CloudVoid>> ChangeUsernameAsync(string accessToken, CloudChangeUsernameRequest body, CancellationToken ct)
    { ChangeUsernameCalls++; return Task.FromResult(OnChangeUsername(accessToken, body)); }

    public Task<CloudApiResult<CloudVoid>> SetPrivateAsync(string accessToken, bool isPrivate, CancellationToken ct)
    { SetPrivateCalls++; return Task.FromResult(OnSetPrivate(accessToken, isPrivate)); }

    public Task<CloudApiResult<CloudVoid>> DeleteAccountAsync(string accessToken, string? currentPassword, CancellationToken ct)
    { DeleteAccountCalls++; return Task.FromResult(OnDeleteAccount(accessToken, currentPassword)); }

    public Task<CloudApiResult<CloudAvatarUploadResponse>> UploadAvatarAsync(string accessToken, byte[] bytes, string contentType, CancellationToken ct)
    { UploadAvatarCalls++; return Task.FromResult(OnUploadAvatar(accessToken, bytes, contentType)); }

    public Task<CloudApiResult<CloudVoid>> PutDeviceAsync(string accessToken, string installId, CloudDevicePutRequest body, CancellationToken ct)
    { PutDeviceCalls++; return Task.FromResult(OnPutDevice(accessToken, installId, body)); }

    public Task<CloudApiResult<List<CloudProfileSummaryDto>>> ListProfilesAsync(string accessToken, CancellationToken ct)
    { ListProfilesCalls++; return Task.FromResult(OnListProfiles(accessToken)); }

    public Task<CloudApiResult<CloudProfileDto>> GetProfileAsync(string accessToken, string profileId, CancellationToken ct)
    { GetProfileCalls++; return Task.FromResult(OnGetProfile(accessToken, profileId)); }

    public Task<CloudApiResult<CloudPutProfileResult>> PutProfileAsync(string accessToken, string profileId, CloudPutProfileRequest body, CancellationToken ct)
    { PutProfileCalls++; return Task.FromResult(OnPutProfile(accessToken, profileId, body)); }

    public Task<CloudApiResult<CloudVoid>> DeleteProfileAsync(string accessToken, string profileId, CancellationToken ct)
    { DeleteProfileCalls++; return Task.FromResult(OnDeleteProfile(accessToken, profileId)); }

    public Task<CloudApiResult<CloudRawResponse>> PostRawAsync(string path, string rawJsonBody, string? accessToken, CancellationToken ct)
    { PostRawCalls++; return Task.FromResult(OnPostRaw(path, rawJsonBody, accessToken)); }

    public Task<CloudApiResult<CloudRawResponse>> SendRawAsync(HttpMethod method, string path, string? rawJsonBody, string? accessToken, CancellationToken ct)
    { SendRawCalls++; return Task.FromResult(OnSendRaw(method, path, rawJsonBody, accessToken)); }
}

internal sealed class InMemoryConfigStore : Nexus.Service.Persistence.IConfigStore
{
    private Nexus.Service.Persistence.NexusSettings _settings = new();

    public string SettingsPath => ":memory:";
    public Nexus.Service.Persistence.NexusSettings Load() => _settings;

    public void Update(Action<Nexus.Service.Persistence.NexusSettings> mutator)
    {
        mutator(_settings);
        OnChanged?.Invoke();
    }

    public void Reload() { _settings = new(); }
    public void FlushNow() { }
    public event Action? OnChanged;
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;
    public ManualTimeProvider(DateTimeOffset utcNow) { _utcNow = utcNow; }
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan by) => _utcNow += by;
}
