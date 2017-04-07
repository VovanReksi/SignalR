using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Redis;
using Microsoft.AspNetCore.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ChatSample
{
    public class RedisPresenceManager<THub> : IDisposable, IPresenceManager where THub : HubWithPresence
    {
        private IHubContext<THub> _hubContext;
        private HubLifetimeManager<THub> _lifetimeManager;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly ConnectionMultiplexer _redisConnection;

        public RedisPresenceManager(IHubContext<THub> hubContext, HubLifetimeManager<THub> lifetimeManager,
            IServiceScopeFactory serviceScopeFactory, IOptions<RedisOptions> options)
        {
            _hubContext = hubContext;
            _lifetimeManager = lifetimeManager;
            _serviceScopeFactory = serviceScopeFactory;
            options.Value.Factory
        }

        private readonly ConcurrentDictionary<Connection, UserDetails> usersOnline
            = new ConcurrentDictionary<Connection, UserDetails>();

        public IEnumerable<UserDetails> UsersOnline => usersOnline.Values;

        public async Task UserJoined(Connection connection)
        {
            // `context.User?.Identity?.Name ?? string.Empty` ?
            var user = new UserDetails(connection.ConnectionId, connection.User.Identity.Name);

            await Notify(hub => hub.OnUserJoined(user));

            usersOnline.TryAdd(connection, user);
        }

        public async Task UserLeft(Connection connection)
        {
            usersOnline.TryRemove(connection, out UserDetails user);

            await Notify(hub => hub.OnUserLeft(user));
        }

        private async Task Notify(Func<THub, Task> invocation)
        {
            foreach (var connection in usersOnline.Keys)
            {
                using (var scope = _serviceScopeFactory.CreateScope())
                {
                    var hubActivator = scope.ServiceProvider.GetRequiredService<IHubActivator<THub, IClientProxy>>();
                    var hub = hubActivator.Create();

                    hub.Clients = _hubContext.Clients;
                    hub.Context = new HubCallerContext(connection);
                    hub.Groups = new GroupManager<THub>(connection, _lifetimeManager);

                    try
                    {
                        await invocation(hub);
                    }
                    catch
                    {
                        // TODO: log
                    }
                    finally
                    {
                        hubActivator.Release(hub);
                    }
                }
            }
        }
    }
}
