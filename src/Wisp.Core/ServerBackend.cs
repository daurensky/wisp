using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Wisp.Core;

public sealed record ServerPresence(UserProfile Profile,string? ChannelId,long SeenAt);
public sealed record ServerConfig(string Url="https://89.126.195.20:8443",string CertificateSha256="")
{
    public Uri BaseUri()
    {
        if(!Uri.TryCreate(Url.TrimEnd('/'),UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.UserInfo.Length!=0||uri.Query.Length!=0||uri.Fragment.Length!=0||uri.AbsolutePath!="/")throw new InvalidDataException("Нужен HTTPS адрес сервера, например https://89.126.195.20:8443.");
        if(CertificateSha256.Length!=64||!CertificateSha256.All(char.IsAsciiHexDigit))throw new InvalidDataException("Нужен SHA-256 сертификата: 64 шестнадцатеричных символа.");
        return uri;
    }
    public VoiceServerSettings VoiceSettings()=>new(true,new UriBuilder(BaseUri()){Scheme="wss",Path="/rtc"}.Uri.AbsoluteUri,CertificateSha256);
    public string DataDirectory(string root)=>Path.Combine(root,"servers",Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BaseUri().AbsoluteUri))).ToLowerInvariant()[..16]);
    public static ServerConfig? Load(string root)
    {
        var path=Path.Combine(root,"server.json");
        if(File.Exists(path))return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(path));
        var old=Path.Combine(root,"voice-server.json");
        if(!File.Exists(old))return null;
        var voice=VoiceServerSettings.Load(root);
        if(voice.CertificateSha256.Length!=64)return null;
        return new(new UriBuilder(voice.Endpoint()){Scheme="https",Path="/"}.Uri.AbsoluteUri.TrimEnd('/'),voice.CertificateSha256);
    }
    public void Save(string root){_=BaseUri();Directory.CreateDirectory(root);File.WriteAllText(Path.Combine(root,"server.json"),JsonSerializer.Serialize(this,new JsonSerializerOptions{WriteIndented=true}));}
}
public sealed class ServerRequestException(HttpStatusCode status,string message):Exception(message)
{
    public HttpStatusCode Status {get;}=status;
}

/// <summary>Certificate-pinned HTTPS accounts, metadata and media backed by the owner's PostgreSQL server.</summary>
public sealed class ServerBackend:IDisposable
{
    private readonly HttpClient http;
    private readonly SemaphoreSlim refreshGate=new(1);
    private readonly TimeProvider time;
    private string? accessToken;
    private DateTimeOffset expires;
    private DateTimeOffset anchor;
    private long anchorTicks;
    public ServerConfig Config {get;}
    public string? RefreshToken{get;private set;}
    public UserProfile? Profile{get;private set;}
    public event Action<string>? SessionChanged;
    public TimeProvider Clock=>time;
    public DateTimeOffset ServerNow()=>anchor==default?time.GetUtcNow():anchor+time.GetElapsedTime(Interlocked.Read(ref anchorTicks));
    public ServerBackend(ServerConfig config,HttpMessageHandler? handler=null,TimeProvider? timeProvider=null)
    {
        Config=config;_=config.BaseUri();time=timeProvider??TimeProvider.System;
        var pin=config.VoiceSettings();
        handler??=new HttpClientHandler{AllowAutoRedirect=false,ServerCertificateCustomValidationCallback=(_,cert,_,errors)=>pin.ValidateCertificate(cert,errors)};
        http=new HttpClient(handler){BaseAddress=config.BaseUri(),Timeout=TimeSpan.FromSeconds(20),MaxResponseContentBufferSize=32*1024*1024};
    }
    private sealed record AuthReply(string AccessToken,string RefreshToken,int ExpiresIn,UserProfile Profile);
    private void Accept(AuthReply auth)
    {
        if(auth.AccessToken.Length!=43||auth.RefreshToken.Length!=43||auth.ExpiresIn<180)throw new InvalidDataException("Некорректная сессия сервера.");
        accessToken=auth.AccessToken;RefreshToken=auth.RefreshToken;Profile=auth.Profile with{AvatarPath=null,BannerPath=null};expires=time.GetUtcNow().AddSeconds(auth.ExpiresIn-120);
        SessionChanged?.Invoke(auth.RefreshToken);
    }
    public async Task<UserProfile> SignInAsync(string email,string password,bool register,string username)
    {
        using var response=await http.PostAsJsonAsync("/v1/auth/"+(register?"register":"login"),new{email,password,username});using var json=await Read(response);
        Accept(json.RootElement.Deserialize<AuthReply>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Ответ сервера не содержит сессии."));return Profile!;
    }
    public async Task LogoutAsync(){if(RefreshToken is null)return;using var response=await http.PostAsJsonAsync("/v1/auth/logout",new{refreshToken=RefreshToken});using var result=await Read(response);RefreshToken=accessToken=null;}
    public async Task<UserProfile> RestoreAsync(string refreshToken){RefreshToken=refreshToken;expires=default;await TokenAsync();return Profile!;}
    public Task<string> GetAccessTokenAsync(CancellationToken token=default)=>TokenAsync().WaitAsync(token);
    private async Task<string> TokenAsync()
    {
        await refreshGate.WaitAsync();
        try
        {
            if(accessToken is not null&&expires>time.GetUtcNow())return accessToken;
            if(RefreshToken is null)throw new InvalidOperationException("Войди в аккаунт сервера.");
            using var response=await http.PostAsJsonAsync("/v1/auth/refresh",new{refreshToken=RefreshToken});using var json=await Read(response);
            Accept(json.RootElement.Deserialize<AuthReply>(new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new InvalidDataException("Некорректный ответ обновления сессии."));return accessToken!;
        }
        finally{refreshGate.Release();}
    }
    private async Task<HttpRequestMessage> Request(HttpMethod method,string path,object? value=null)
    {
        if(path.Split('/').Any(p=>!Regex.IsMatch(p,"^[A-Za-z0-9_-]{1,128}$")))throw new ArgumentException("Некорректный путь данных.");
        var request=new HttpRequestMessage(method,"/v1/data/"+path);request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await TokenAsync());
        if(value is not null)request.Content=JsonContent.Create(value,options:new JsonSerializerOptions());return request;
    }
    public async Task<T?> GetAsync<T>(string path,CancellationToken token=default)
    {
        using var request=await Request(HttpMethod.Get,path);using var response=await http.SendAsync(request,token);using var json=await Read(response);
        return json.RootElement.ValueKind==JsonValueKind.Null?default:json.RootElement.Deserialize<T>();
    }
    public async Task PutAsync<T>(string path,T value,CancellationToken token=default)
    {
        using var request=await Request(HttpMethod.Put,path,value);using var response=await http.SendAsync(request,token);using var json=await Read(response);
    }
    public async Task<bool> TryCreateAsync<T>(string path,T value)
    {
        using var request=await Request(HttpMethod.Put,path,value);request.Headers.TryAddWithoutValidation("If-Match","null_etag");using var response=await http.SendAsync(request);
        if(response.StatusCode==HttpStatusCode.PreconditionFailed)return false;using var json=await Read(response);return true;
    }
    public async Task DeleteAsync(string path,CancellationToken token=default)
    {
        using var request=await Request(HttpMethod.Delete,path);using var response=await http.SendAsync(request,token);using var json=await Read(response);
    }
    public async Task SaveProfileAsync(UserProfile profile){if(profile.Id!=Profile?.Id)throw new InvalidOperationException("Чужой профиль.");await PutAsync($"profiles/{profile.Id}/Info",profile with{AvatarPath=null,BannerPath=null});Profile=profile;}
    public Task CreateCommunityAsync(CommunitySnapshot snapshot)=>PutAsync($"communities/{snapshot.Community.Id}/snapshot",snapshot with{Members=snapshot.Members.Select(p=>p with{AvatarPath=null,BannerPath=null}).ToList()});
    private async Task<JsonDocument> ApiPost(string path,object value)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,path){Content=JsonContent.Create(value)};request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await TokenAsync());using var response=await http.SendAsync(request);return await Read(response);
    }
    public async Task<string> CreateInviteAsync(string community){using var json=await ApiPost("/v1/invites/create",new{community});return "wisp-invite:"+json.RootElement.GetProperty("token").GetString();}
    public async Task<string> JoinCommunityAsync(string invite){using var json=await ApiPost("/v1/invites/accept",new{token=invite.StartsWith("wisp-invite:",StringComparison.Ordinal)?invite[12..]:invite});return json.RootElement.GetProperty("community").GetString()!;}
    public async Task<List<CommunitySnapshot>> CommunitiesAsync()
    {
        var ids=await GetAsync<Dictionary<string,bool>>($"users/{Profile!.Id}/communities")??new();var result=new List<CommunitySnapshot>();
        foreach(var id in ids.Keys){var snapshot=await GetAsync<CommunitySnapshot>($"communities/{id}/snapshot");if(snapshot is not null)result.Add(snapshot);}return result;
    }
    public Task PresenceAsync(string cid,UserProfile profile,string? channel)=>PutAsync($"communities/{cid}/presence/{Profile!.Id}",new ServerPresence(profile with{AvatarPath=null,BannerPath=null},channel,ServerNow().ToUnixTimeMilliseconds()));
    public Task SendMessageAsync(string cid,ChatMessage message)=>PutAsync($"communities/{cid}/messages/{message.ChannelId}/{message.Id}",message);
    public async Task<List<ChatMessage>> MessagesAsync(string cid,string channel)
    {
        var data=await GetAsync<Dictionary<string,ChatMessage>>($"communities/{cid}/messages/{channel}")??new();return data.Values.OrderBy(m=>m.SentAt).ToList();
    }
    private async Task<JsonDocument> Read(HttpResponseMessage response)
    {
        if(response.Headers.Date is{} date){anchor=date.AddMilliseconds(500);Interlocked.Exchange(ref anchorTicks,time.GetTimestamp());}
        var body=await response.Content.ReadAsStringAsync();
        if(!response.IsSuccessStatusCode)
        {
            string message=response.StatusCode switch{HttpStatusCode.Unauthorized=>"Неверные данные входа или срок сессии истёк.",HttpStatusCode.Forbidden=>"Нет доступа к этому действию или регистрация закрыта владельцем.",HttpStatusCode.Conflict=>"Аккаунт уже существует либо данные конфликтуют.",HttpStatusCode.TooManyRequests=>"Слишком много запросов. Подожди минуту.",HttpStatusCode.NotFound=>"Данные не найдены. Проверь адрес и версию сервера.",_=>$"Сервер: {(int)response.StatusCode}. Проверь соединение и обновление службы."};
            throw new ServerRequestException(response.StatusCode,message);
        }
        return JsonDocument.Parse(body);
    }
    public void Dispose(){http.Dispose();refreshGate.Dispose();}
}
