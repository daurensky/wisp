using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wisp.Core;

public sealed record FirebaseConfig(string ApiKey, string ProjectId, string DatabaseUrl);
public sealed record FirebasePresence(UserProfile Profile, string? ChannelId, long SeenAt);

/// <summary>Firebase Authentication, RTDB metadata and cached attachments. Live voice never goes through the database.</summary>
public sealed partial class FirebaseBackend : IDisposable
{
    private readonly HttpClient http;
    private readonly FirebaseConfig config;
    private readonly SemaphoreSlim refreshGate = new(1);
    private string? idToken;
    private DateTimeOffset expires;
    public string? RefreshToken { get; private set; }
    public UserProfile? Profile { get; private set; }
    public FirebaseBackend(FirebaseConfig config, HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKey) || !Regex.IsMatch(config.ProjectId, "^[a-z0-9-]+$") ||
            !Uri.TryCreate(config.DatabaseUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            !(url.Host.EndsWith(".firebaseio.com") || url.Host.EndsWith(".firebasedatabase.app")) || url.Query.Length != 0)
            throw new ArgumentException("Некорректная конфигурация Firebase.");
        this.config = config;
        time = timeProvider ?? TimeProvider.System;
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(20);
    }
    public static FirebaseBackend? Load(string root)
    {
        var path = Path.Combine(root, "firebase.json");
        if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "firebase.json");
        return File.Exists(path) ? new(JsonSerializer.Deserialize<FirebaseConfig>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("firebase.json")) : null;
    }
    public async Task<UserProfile> SignInAsync(string email, string password, bool register, string username)
    {
        using var response = await http.PostAsJsonAsync("https://identitytoolkit.googleapis.com/v1/accounts:" + (register ? "signUp" : "signInWithPassword") + "?key=" + Uri.EscapeDataString(config.ApiKey), new { email, password, returnSecureToken = true });
        using var data = await Read(response);
        idToken = data.RootElement.GetProperty("idToken").GetString();
        RefreshToken = data.RootElement.GetProperty("refreshToken").GetString(); expires = time.GetUtcNow().AddMinutes(50);
        var uid = data.RootElement.GetProperty("localId").GetString()!;
        var existing = await GetAsync<UserProfile>($"profiles/{uid}/Info") ?? await GetAsync<UserProfile>($"profiles/{uid}");
        Profile = existing is null ? new UserProfile(uid, string.IsNullOrWhiteSpace(username) ? "user" : username) : existing with { Id = uid, AvatarPath = null, BannerPath = null };
        if (existing is null || register) await SaveProfileAsync(Profile);
        return Profile;
    }
    public async Task<UserProfile> RestoreAsync(string refreshToken)
    {
        RefreshToken = refreshToken; expires = DateTimeOffset.MinValue; await TokenAsync();
        var uid = Profile!.Id;
        var restored = await GetAsync<UserProfile>($"profiles/{uid}/Info") ?? await GetAsync<UserProfile>($"profiles/{uid}") ?? throw new InvalidDataException("Профиль Firebase не найден.");
        return Profile = restored with { Id = uid, AvatarPath = null, BannerPath = null };
    }
    /// <summary>A short-lived ID token for the configured SFU; never persist or log it.</summary>
    public Task<string> GetAccessTokenAsync(CancellationToken cancellation = default) => TokenAsync().WaitAsync(cancellation);

    private async Task<string> TokenAsync()
    {
        await refreshGate.WaitAsync();
        try
        {
            if (idToken is not null && expires > time.GetUtcNow()) return idToken;
            if (RefreshToken is null) throw new InvalidOperationException("Войди в Firebase.");
            using var response = await http.PostAsync("https://securetoken.googleapis.com/v1/token?key=" + Uri.EscapeDataString(config.ApiKey), new FormUrlEncodedContent(new Dictionary<string,string> { ["grant_type"] = "refresh_token", ["refresh_token"] = RefreshToken }));
            using var data = await Read(response); idToken = data.RootElement.GetProperty("id_token").GetString()!;
            RefreshToken = data.RootElement.GetProperty("refresh_token").GetString(); expires = time.GetUtcNow().AddMinutes(50);
            var uid = data.RootElement.GetProperty("user_id").GetString()!;
            if (Profile?.Id != uid) Profile = new UserProfile(uid, "user");
            return idToken;
        }
        finally { refreshGate.Release(); }
    }
    public async Task<T?> GetAsync<T>(string path, CancellationToken cancellation = default)
    {
        using var response = await http.GetAsync(await Url(path), cancellation); using var json = await Read(response);
        return json.RootElement.ValueKind == JsonValueKind.Null ? default : json.RootElement.Deserialize<T>();
    }
    public async Task PutAsync<T>(string path, T value, CancellationToken cancellation = default)
    {
        using var response = await http.PutAsJsonAsync(await Url(path), value, new JsonSerializerOptions(), cancellation); using var result = await Read(response);
    }
    /// <summary>Firebase REST conditional PUT: create exactly once, without replacing an existing object.</summary>
    public async Task<bool> TryCreateAsync<T>(string path, T value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, await Url(path))
        {
            Content = JsonContent.Create(value, options: new JsonSerializerOptions())
        };
        request.Headers.TryAddWithoutValidation("if-match", "null_etag");
        using var response = await http.SendAsync(request);
        if (response.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
            return false;
        using var result = await Read(response);
        return true;
    }
    public async Task DeleteAsync(string path, CancellationToken cancellation = default)
    {
        using var response = await http.DeleteAsync(await Url(path), cancellation); using var result = await Read(response);
    }
    public async Task PatchAsync<T>(string path, T value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, await Url(path)) { Content = JsonContent.Create(value, options: new JsonSerializerOptions()) };
        using var response = await http.SendAsync(request); using var result = await Read(response);
    }
    public async Task SaveProfileAsync(UserProfile p)
    {
        if (p.Id != Profile?.Id) throw new InvalidOperationException("Чужой профиль.");
        await PatchAsync($"profiles/{p.Id}", new
        {
            p.Id,
            p.Username,
            Info = p with { AvatarPath = null, BannerPath = null }
        }); Profile = p;
    }
    public async Task CreateCommunityAsync(CommunitySnapshot snapshot)
    {
        var id = snapshot.Community.Id;
        await PutAsync($"communities/{id}/snapshot", snapshot with { Members = snapshot.Members.Select(p => p with { AvatarPath = null, BannerPath = null }).ToList() });
        await JoinCommunityAsync(id);
    }
    public async Task JoinCommunityAsync(string id)
    {
        await PutAsync($"communities/{id}/members/{Profile!.Id}", true);
        await PutAsync($"users/{Profile.Id}/communities/{id}", true);
    }
    public async Task<List<CommunitySnapshot>> CommunitiesAsync()
    {
        var ids = await GetAsync<Dictionary<string,bool>>($"users/{Profile!.Id}/communities") ?? new(); var list = new List<CommunitySnapshot>();
        foreach (var id in ids.Keys) { var s = await GetAsync<CommunitySnapshot>($"communities/{id}/snapshot"); if (s is not null) list.Add(s); }
        return list;
    }
    public Task PresenceAsync(string communityId, UserProfile p, string? channel) => PutAsync($"communities/{communityId}/presence/{Profile!.Id}", new FirebasePresence(p with { AvatarPath = null, BannerPath = null }, channel, ServerNow().ToUnixTimeMilliseconds()));
    public Task SendMessageAsync(string communityId, ChatMessage message) => PutAsync($"communities/{communityId}/messages/{message.ChannelId}/{message.Id}", FirebaseChatMessage.From(message));
    public async Task<List<ChatMessage>> MessagesAsync(string community, string channel)
    {
        using var response = await http.GetAsync(await Url($"communities/{community}/messages/{channel}") + "&orderBy=%22SentAt%22&limitToLast=100");
        using var data = await Read(response);
        return data.RootElement.ValueKind == JsonValueKind.Null ? new() : data.RootElement.EnumerateObject().Select(item => FirebaseChatMessage.Read(item.Value)).ToList();
    }
    private async Task<string> Url(string path)
    {
        if (path.Split('/').Any(p => !Regex.IsMatch(p, "^[a-zA-Z0-9_-]+$"))) throw new ArgumentException("Недопустимый путь Firebase.");
        return config.DatabaseUrl.TrimEnd('/') + "/" + path + ".json?auth=" + Uri.EscapeDataString(await TokenAsync());
    }
    private async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        ObserveServerDate(response);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var message = text.Contains("EMAIL_EXISTS") ? "Этот email уже зарегистрирован. Нажми «Войти»."
                : text.Contains("INVALID_LOGIN_CREDENTIALS") || text.Contains("INVALID_PASSWORD") ? "Неверный email или пароль."
                : text.Contains("OPERATION_NOT_ALLOWED") ? "Включи Email/Password в Firebase Authentication."
                : text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase) ? "Доступ Firebase запрещён. Проверь вход, членство в сообществе и опубликованные правила доступа."
                : $"Firebase: {(int)response.StatusCode}. Проверь вход, настройки проекта и правила доступа.";
            var failure = text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                ? FirebaseFailure.PermissionDenied
                : response.StatusCode is System.Net.HttpStatusCode.Unauthorized ? FirebaseFailure.Authentication
                : (int)response.StatusCode >= 500 || (int)response.StatusCode == 429 ? FirebaseFailure.Unavailable
                : FirebaseFailure.InvalidRequest;
            throw new FirebaseRequestException(response.StatusCode, failure, message);
        }
        return JsonDocument.Parse(text);
    }
    public void Dispose() { http.Dispose(); refreshGate.Dispose(); }
}
