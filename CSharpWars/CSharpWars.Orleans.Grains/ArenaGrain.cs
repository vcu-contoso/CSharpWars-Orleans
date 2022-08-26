﻿using CSharpWars.Enums;
using CSharpWars.Orleans.Contracts.Arena;
using CSharpWars.Orleans.Contracts.Bot;
using CSharpWars.Orleans.Grains.Helpers;
using Microsoft.Extensions.Configuration;
using Orleans;
using Orleans.Runtime;

namespace CSharpWars.Orleans.Grains;

public class ArenaState
{
    public bool Exists { get; set; }
    public string Name { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public IList<Guid>? BotIds { get; set; }
}

public interface IArenaGrain : IGrainWithStringKey
{
    Task<List<BotDto>> GetAllActiveBots();
    Task<List<BotDto>> GetAllLiveBots();

    Task<ArenaDto> GetArenaDetails();

    Task<BotDto> CreateBot(string playerName, BotToCreateDto bot);
    Task DeleteArena();
    Task DeleteBot(Guid botId);
}

public class ArenaGrain : Grain, IArenaGrain
{
    private readonly IGrainFactoryHelperWithStringKey<IPlayerGrain> _playerGrainFactory;
    private readonly IGrainFactoryHelperWithGuidKey<IBotGrain> _botGrainFactory;
    private readonly IGrainFactoryHelperWithStringKey<IProcessingGrain> _processingGrainFactory;
    private readonly IPersistentState<ArenaState> _state;
    private readonly IConfiguration _configuration;

    public ArenaGrain(
        IGrainFactoryHelperWithStringKey<IPlayerGrain> playerGrainFactory,
        IGrainFactoryHelperWithGuidKey<IBotGrain> botGrainFactory,
        IGrainFactoryHelperWithStringKey<IProcessingGrain> processingGrainFactory,
        [PersistentState("arena", "arenaStore")] IPersistentState<ArenaState> state,
        IConfiguration configuration)
    {
        _playerGrainFactory = playerGrainFactory;
        _botGrainFactory = botGrainFactory;
        _processingGrainFactory = processingGrainFactory;
        _state = state;
        _configuration = configuration;
    }

    public async Task<List<BotDto>> GetAllActiveBots()
    {
        var activeBots = await GetBots(false);

        await PingProcessor();

        return activeBots;
    }

    public async Task<List<BotDto>> GetAllLiveBots()
    {
        return await GetBots(true);
    }

    private async Task<List<BotDto>> GetBots(bool onlyLive)
    {
        if (!_state.State.Exists)
        {
            _ = await GetArenaDetails();
        }

        var activeBots = new List<BotDto>();

        if (_state.State.BotIds != null)
        {
            foreach (var botId in _state.State.BotIds)
            {
                var botState = await _botGrainFactory.FromGrain(botId, g => g.GetState());

                if (!onlyLive || botState.Move != Move.Died)
                {
                    activeBots.Add(botState);
                }
            }
        }

        await PingProcessor();

        return activeBots;
    }

    public async Task<ArenaDto> GetArenaDetails()
    {
        if (!_state.State.Exists)
        {
            _state.State.Name = this.GetPrimaryKeyString();
            _state.State.Width = _configuration.GetValue<int>("ARENA_WIDTH");
            _state.State.Height = _configuration.GetValue<int>("ARENA_HEIGHT");
            _state.State.BotIds = new List<Guid>();
            _state.State.Exists = true;
            await _state.WriteStateAsync();
        }

        await PingProcessor();

        return new ArenaDto(_state.State.Name, _state.State.Width, _state.State.Height);
    }

    public async Task<BotDto> CreateBot(string playerName, BotToCreateDto bot)
    {
        if (!_state.State.Exists || _state.State.BotIds == null)
        {
            throw new ArgumentNullException();
        }

        var botId = Guid.NewGuid();

        await _playerGrainFactory.FromGrain(playerName, async playerGrain =>
        {
            await playerGrain.ValidateBotDeploymentLimit();
            await playerGrain.BotCeated(botId);
        });

        var createdBot = await _botGrainFactory.FromGrain(botId, g => g.CreateBot(bot));

        _state.State.BotIds.Add(botId);
        await _state.WriteStateAsync();

        return createdBot;
    }

    public async Task DeleteArena()
    {
        if (_state.State.Exists && _state.State.BotIds != null)
        {
            await _processingGrainFactory.FromGrain(_state.State.Name, g => g.Stop());

            await Task.Delay(2000);

            foreach (var botId in _state.State.BotIds)
            {
                await _botGrainFactory.FromGrain(botId, g => g.DeleteBot(true));
            }

            await _state.ClearStateAsync();
        }

        DeactivateOnIdle();
    }

    public Task DeleteBot(Guid botId)
    {
        if (_state.State.Exists && _state.State.BotIds != null)
        {
            _state.State.BotIds.Remove(botId);
        }

        return Task.CompletedTask;
    }

    private async Task PingProcessor()
    {
        await _processingGrainFactory.FromGrain(_state.State.Name, g => g.Ping());
    }
}