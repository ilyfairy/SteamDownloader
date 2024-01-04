using SteamKit2;
using SteamKit2.Authentication;

namespace SteamDownloader;

public partial class SteamSession
{
    /// <summary>
    /// 管理用户登录, 无需重新登录
    /// </summary>
    public class SteamAuthentication
    {
        private readonly SteamSession steam;

        /// <summary>
        /// 是否是登录状态
        /// </summary>
        public bool Logged => steam.steamUser.SteamID is not null;
        public string? AccessToken { get; private set; }

        private bool isAnonymous;
        private string? username;

        public SteamAuthentication(SteamSession steamSession)
        {
            this.steam = steamSession;

            //steamSession.CallbackManager.Subscribe<SteamClient.ConnectedCallback>(v =>
            //{
            //    Logged = true;
            //});
            steamSession.CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(v =>
            {
                steamSession.connectionLoginResult = v.Result;
            });
            steamSession.CallbackManager.Subscribe<SteamUser.LoggedOffCallback>(v =>
            {
                steamSession.connectionLoginResult = v.Result;
            });

        }

        /// <summary>
        /// 匿名登录
        /// </summary>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public async Task LoginAnonymousAsync(CancellationToken cancellationToken = default)
        {
            if (steam.SteamClient.IsConnected is false)
            {
                await steam.ConnectAsync(cancellationToken);
            }

            if (steam.steamUser.SteamID is not null)
                return;

            try
            {
                await steam.loginLock.WaitAsync(cancellationToken);

                steam.connectionLoginResult = EResult.Invalid;
                AccessToken = null;
                steam.steamUser.LogOnAnonymous();

                await Task.Run(steam.CallbackManager.RunWaitCallbacks, cancellationToken);

                if (steam.connectionLoginResult is EResult.OK)
                {
                    isAnonymous = true;
                }
                else if (steam.connectionLoginResult is EResult.NoConnection)
                {
                    throw new ConnectionException("没有连接, 请先连接");
                }
                else
                {
                    throw new ConnectionException($"登录失败: {steam.connectionLoginResult}");
                }
            }
            finally
            {
                steam.loginLock.Release();
            }
        }

        /// <summary>
        /// 登录
        /// </summary>
        /// <param name="username">用户名</param>
        /// <param name="password">密码</param>
        /// <param name="shouldRememberPassword">是否记住密码(在此之后可以用AccessToken登录)</param>
        /// <returns></returns>
        public async Task LoginAsync(string username, string password, bool shouldRememberPassword, CancellationToken cancellationToken = default)
        {
            if (!steam.SteamClient.IsConnected)
            {
                await steam.ConnectAsync(cancellationToken);
            }

            if (steam.steamUser.SteamID is not null)
                return;

            try
            {
                await steam.loginLock.WaitAsync(cancellationToken);

                var authSession = await steam.SteamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails()
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = shouldRememberPassword,
                    Authenticator = new UserConsoleAuthenticator(),
                });

                using var _ = steam.CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(v =>
                {
                    steam.connectionLoginResult = v.Result;
                });

                var result = await authSession.PollingWaitForResultAsync(cancellationToken);

                AccessToken = result.RefreshToken;
                username = result.AccountName;
                steam.steamUser.LogOn(new SteamUser.LogOnDetails()
                {
                    Username = result.AccountName,
                    Password = null,
                    AccessToken = result.RefreshToken,
                    ShouldRememberPassword = shouldRememberPassword,
                });

                while (true)
                {
                    steam.CallbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan);
                    if (steam.connectionLoginResult is EResult.OK)
                    {
                        break;
                    }    
                    if (steam.connectionLoginResult is EResult.NoConnection)
                        throw new ConnectionException("登录失败");
                    await Task.Delay(100, cancellationToken);
                }
            }
            finally
            {
                steam.loginLock.Release();
            }
        }

        public async Task LoginFromAccessTokenAsync(string username, string accessToken, CancellationToken cancellationToken = default)
        {
            if (!steam.SteamClient.IsConnected)
            {
                await steam.ConnectAsync(cancellationToken);
            }

            if (steam.steamUser.SteamID is not null)
                return;

            try
            {
                await steam.loginLock.WaitAsync(cancellationToken);

                using var loggedOnCallbackDisposable = steam.CallbackManager.Subscribe<SteamUser.LoggedOnCallback>(v =>
                {
                    steam.connectionLoginResult = v.Result;
                });

                steam.connectionLoginResult = EResult.Invalid;

                steam.steamUser.LogOn(new SteamUser.LogOnDetails()
                {
                    Username = username,
                    Password = null,
                    AccessToken = accessToken,
                    ShouldRememberPassword = true,
                });

                while (true)
                {
                    steam.CallbackManager.RunWaitAllCallbacks(Timeout.InfiniteTimeSpan);
                    if (steam.connectionLoginResult is EResult.OK)
                    {
                        AccessToken = accessToken;
                        this.username = username;
                        break;
                    }
                    if (steam.connectionLoginResult is EResult.NoConnection)
                        throw new ConnectionException("登录失败");
                    await Task.Delay(50, cancellationToken);
                }

            }
            finally
            {
                steam.loginLock.Release();
            }
        }

        public async Task EnsureLoginAsync(CancellationToken cancellationToken = default)
        {
            if (Logged)
                return;

            if (isAnonymous)
            {
                await LoginAnonymousAsync(cancellationToken);
            }
            else
            {
                if (username is null || AccessToken is null)
                    throw new ConnectionException("请先登录");

                await LoginFromAccessTokenAsync(username, AccessToken, cancellationToken);
            }
        }
    }

}
