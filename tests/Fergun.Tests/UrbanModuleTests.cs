﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutoBogus;
using AutoBogus.Moq;
using Bogus;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Fergun.Apis.Urban;
using Fergun.Interactive;
using Fergun.Modules;
using Moq;
using Moq.Protected;
using Xunit;

namespace Fergun.Tests;

public class UrbanModuleTests
{
    private readonly Mock<IInteractionContext> _contextMock = new();
    private readonly Mock<IDiscordInteraction> _interactionMock = new();
    private readonly Mock<IUrbanDictionary> _urbanDictionaryMock = CreateMockedUrbanDictionary();
    private readonly Mock<UrbanModule> _urbanModuleMock;
    private readonly DiscordSocketClient _client = new();
    private readonly InteractiveService _interactive;

    public UrbanModuleTests()
    {
        _interactive = new InteractiveService(_client);
        _urbanModuleMock = new Mock<UrbanModule>(() => new UrbanModule(_urbanDictionaryMock.Object, _interactive));
        _contextMock.SetupGet(x => x.Interaction).Returns(_interactionMock.Object);
        _contextMock.SetupGet(x => x.User).Returns(() => AutoFaker.Generate<IUser>(b => b.WithBinder(new MoqBinder())));
        ((IInteractionModuleBase)_urbanModuleMock.Object).SetContext(_contextMock.Object);
    }

    [MemberData(nameof(GetRandomWords))]
    [Theory]
    public async Task Search_Calls_GetDefinitionsAsync(string term)
    {
        var module = _urbanModuleMock.Object;

        await module.Search(term);

        _urbanModuleMock.Protected().Verify<Task>("DeferAsync", Times.Once(), ItExpr.IsAny<bool>(), ItExpr.IsAny<RequestOptions>());
        _urbanDictionaryMock.Verify(u => u.GetDefinitionsAsync(It.Is<string>(x => x == term)), Times.Once);
        int count = (await _urbanDictionaryMock.Object.GetDefinitionsAsync(It.IsAny<string>())).Count;

        if (count == 0)
        {
            _interactionMock.Verify(i => i.FollowupAsync(It.IsAny<string>(), It.IsAny<Embed[]>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<AllowedMentions>(),
                It.IsAny<MessageComponent>(), It.IsAny<Embed>(), It.IsAny<RequestOptions>()), Times.Once);
        }
    }

    [Fact]
    public async Task Random_Calls_GetRandomDefinitionsAsync()
    {
        var module = _urbanModuleMock.Object;

        await module.Random();

        _urbanModuleMock.Protected().Verify<Task>("DeferAsync", Times.Once(), ItExpr.IsAny<bool>(), ItExpr.IsAny<RequestOptions>());
        _urbanDictionaryMock.Verify(u => u.GetRandomDefinitionsAsync(), Times.Once);
    }

    [Fact]
    public async Task WordsOfTheDay_Calls_GetWordsOfTheDayAsync()
    {
        var module = _urbanModuleMock.Object;

        await module.WordsOfTheDay();

        _urbanModuleMock.Protected().Verify<Task>("DeferAsync", Times.Once(), ItExpr.IsAny<bool>(), ItExpr.IsAny<RequestOptions>());
        _urbanDictionaryMock.Verify(u => u.GetWordsOfTheDayAsync(), Times.Once);
    }

    [Fact]
    public async Task Invalid_SearchType_Throws_ArgumentException()
    {
        var module = _urbanModuleMock.Object;

        var task = module.SearchAndSendAsync((UrbanModule.UrbanSearchType)3);

        await Assert.ThrowsAsync<ArgumentException>(() => task);
    }

    private static IEnumerable<object?[]> GetRandomWords() => AutoFaker.Generate<string>(20).Select(x => new object[] { x });

    private static Mock<IUrbanDictionary> CreateMockedUrbanDictionary()
    {
        var faker = new Faker();
        var mock = new Mock<IUrbanDictionary>();

        mock.Setup(u => u.GetDefinitionsAsync(It.IsAny<string>())).ReturnsAsync(AutoFaker.Generate<UrbanDefinition>(10).OrDefault(faker, defaultValue: new()));
        mock.Setup(u => u.GetRandomDefinitionsAsync()).ReturnsAsync(AutoFaker.Generate<UrbanDefinition>(10));
        mock.Setup(u => u.GetDefinitionAsync(It.IsAny<int>())).ReturnsAsync(AutoFaker.Generate<UrbanDefinition>());
        mock.Setup(u => u.GetWordsOfTheDayAsync()).ReturnsAsync(AutoFaker.Generate<UrbanDefinition>(10));
        mock.Setup(u => u.GetAutocompleteResultsAsync(It.IsAny<string>())).ReturnsAsync(AutoFaker.Generate<string>(20));
        mock.Setup(u => u.GetAutocompleteResultsExtraAsync(It.IsAny<string>())).ReturnsAsync(AutoFaker.Generate<UrbanAutocompleteResult>(20));

        return mock;
    }
}