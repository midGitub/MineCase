﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MineCase.Engine;
using MineCase.Protocol.Play;
using MineCase.Server.Components;
using MineCase.Server.Game.Entities;
using MineCase.Server.Network.Play;
using MineCase.Server.Persistence.Components;
using MineCase.Server.Settings;
using MineCase.Server.User;
using MineCase.Server.World;
using MineCase.World;
using Orleans;
using Orleans.Concurrency;

namespace MineCase.Server.Game
{
    [Reentrant]
    internal class GameSession : DependencyObject, IGameSession
    {
        private IWorld _world;
        private FixedUpdateComponent _fixedUpdate;
        private readonly Dictionary<IUser, UserContext> _users = new Dictionary<IUser, UserContext>();

        private ILogger _logger;

        public override async Task OnActivateAsync()
        {
            await base.OnActivateAsync();
            _logger = ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<GameSession>();
            _world = await GrainFactory.GetGrain<IWorldAccessor>(0).GetWorld(this.GetPrimaryKeyString());
            await _fixedUpdate.Start(_world);
        }

        protected override void InitializeComponents()
        {
            SetComponent(new PeriodicSaveStateComponent(TimeSpan.FromMinutes(1)));

            _fixedUpdate = new FixedUpdateComponent();
            _fixedUpdate.Tick += OnFixedUpdate;
            SetComponent(_fixedUpdate);
        }

        private async Task OnFixedUpdate(object sender, GameTickArgs e)
        {
            await _world.OnGameTick(e);
            await Task.WhenAll(from u in _users.Keys
                               select u.OnGameTick(e));
        }

        public async Task JoinGame(IUser user)
        {
            var sink = await user.GetClientPacketSink();
            var generator = new ClientPlayPacketGenerator(sink);
            var settings = await GrainFactory.GetGrain<IServerSettings>(0).GetSettings();

            _users[user] = new UserContext
            {
                Generator = generator
            };

            await user.JoinGame();
            await generator.JoinGame(
                await (await user.GetPlayer()).GetEntityId(),
                await user.GetGameMode(),
                Dimension.Overworld,
                Difficulty.Easy,
                (byte)settings.MaxPlayers,
                LevelTypes.Default,
                false);
            await user.NotifyLoggedIn();
            await SendWholePlayersList(user);
        }

        public Task LeaveGame(IUser user)
        {
            _users.Remove(user);
            return BroadcastRemovePlayerFromList(user);
        }

        private async Task BroadcastRemovePlayerFromList(IUser user)
        {
            var players = new List<IPlayer> { await user.GetPlayer() };
            await Task.WhenAll(from u in _users.Keys select u.RemovePlayerList(players));
        }

        public async Task SendWholePlayersList(IUser user)
        {
            var list = await Task.WhenAll(from p in _users.Keys
                                          select p.GetPlayer());

            await user.UpdatePlayerList(list);
        }

        public async Task SendChatMessage(IUser sender, string message)
        {
            var senderName = await sender.GetName();

            // TODO command parser
            // construct name
            Chat jsonData = await CreateStandardChatMessage(senderName, message);
            byte position = 0; // It represents user message in chat box
            foreach (var item in _users.Keys)
            {
                await item.SendChatMessage(jsonData, position);
            }
        }

        public async Task SendChatMessage(IUser sender, IUser receiver, string message)
        {
            var senderName = await sender.GetName();
            var receiverName = await receiver.GetName();

            Chat jsonData = await CreateStandardChatMessage(senderName, message);
            byte position = 0; // It represents user message in chat box
            foreach (var item in _users.Keys)
            {
                if (await item.GetName() == receiverName ||
                    await item.GetName() == senderName)
                    await item.SendChatMessage(jsonData, position);
            }
        }

        private Task<Chat> CreateStandardChatMessage(string name, string message)
        {
            StringComponent nameComponent = new StringComponent(name);
            nameComponent.ClickEvent = new ChatClickEvent(ClickEventType.SuggestCommand, "/msg " + name);
            nameComponent.HoverEvent = new ChatHoverEvent(HoverEventType.ShowEntity, name);
            nameComponent.Insertion = name;

            // construct message
            StringComponent messageComponent = new StringComponent(message);

            // list
            List<ChatComponent> list = new List<ChatComponent>();
            list.Add(nameComponent);
            list.Add(messageComponent);

            Chat jsonData = new Chat(new TranslationComponent("chat.type.text", list));
            return Task.FromResult(jsonData);
        }

        public Task<int> UserNumber()
        {
            return Task.FromResult(_users.Count);
        }

        private class UserContext
        {
            public ClientPlayPacketGenerator Generator { get; set; }
        }
    }
}
