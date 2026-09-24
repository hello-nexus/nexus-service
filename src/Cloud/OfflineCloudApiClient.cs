using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Cloud;

namespace Nexus.Service.Cloud;

/// <summary>Registered in place of <see cref="CloudApiClient"/> when the build carries no credential: every call reports Offline, which the Cloud subsystem already handles, and no socket is opened.</summary>
internal sealed class OfflineCloudApiClient : ICloudApiClient
{
    private const string Reason = "cloud is unavailable in an unofficial build";

    private static Task<CloudApiResult<T>> Offline<T>() =>
        Task.FromResult(CloudApiResult<T>.NetworkError(Reason));

    public Task<CloudApiResult<CloudVoid>> RegisterAsync(CloudRegisterRequest body, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudAuthSession>> LoginAsync(CloudLoginRequest body, CancellationToken ct) => Offline<CloudAuthSession>();

    public Task<CloudApiResult<CloudAuthSession>> RefreshAsync(string refreshToken, CancellationToken ct) => Offline<CloudAuthSession>();

    public Task<CloudApiResult<CloudVoid>> LogoutAsync(string refreshToken, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudRecoveryStartResponse>> RecoveryStartAsync(CloudRecoveryStartRequest body, CancellationToken ct) => Offline<CloudRecoveryStartResponse>();

    public Task<CloudApiResult<CloudRecoveryPollResponse>> RecoveryPollAsync(CloudRecoveryPollRequest body, CancellationToken ct) => Offline<CloudRecoveryPollResponse>();

    public Task<CloudApiResult<CloudVoid>> ChangePasswordAsync(string accessToken, CloudChangePasswordRequest body, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudVoid>> ChangeUsernameAsync(string accessToken, CloudChangeUsernameRequest body, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudVoid>> SetPrivateAsync(string accessToken, bool isPrivate, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudVoid>> DeleteAccountAsync(string accessToken, string? currentPassword, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudAvatarUploadResponse>> UploadAvatarAsync(string accessToken, byte[] bytes, string contentType, CancellationToken ct) => Offline<CloudAvatarUploadResponse>();

    public Task<CloudApiResult<CloudVoid>> PutDeviceAsync(string accessToken, string installId, CloudDevicePutRequest body, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<List<CloudDeviceDto>>> ListDevicesAsync(string accessToken, CancellationToken ct) => Offline<List<CloudDeviceDto>>();

    public Task<CloudApiResult<List<CloudProfileSummaryDto>>> ListProfilesAsync(string accessToken, CancellationToken ct) => Offline<List<CloudProfileSummaryDto>>();

    public Task<CloudApiResult<CloudProfileDto>> GetProfileAsync(string accessToken, string installId, string profileId, CancellationToken ct) => Offline<CloudProfileDto>();

    public Task<CloudApiResult<CloudPutProfileResult>> PutProfileAsync(string accessToken, string installId, string profileId, CloudPutProfileRequest body, CancellationToken ct) => Offline<CloudPutProfileResult>();

    public Task<CloudApiResult<CloudVoid>> DeleteProfileAsync(string accessToken, string installId, string profileId, CancellationToken ct) => Offline<CloudVoid>();

    public Task<CloudApiResult<CloudRawResponse>> PostRawAsync(string path, string rawJsonBody, string? accessToken, CancellationToken ct) => Offline<CloudRawResponse>();

    public Task<CloudApiResult<CloudRawResponse>> SendRawAsync(HttpMethod method, string path, string? rawJsonBody, string? accessToken, CancellationToken ct) => Offline<CloudRawResponse>();
}
