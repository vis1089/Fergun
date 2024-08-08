﻿using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;
using Fergun.Apis.Bing;
using Fergun.Apis.Google;
using Fergun.Apis.Yandex;
using Fergun.Extensions;
using Fergun.Interactive;
using Fergun.Interactive.Selection;
using Fergun.Preconditions;
using Humanizer;
using Microsoft.Extensions.Logging;

namespace Fergun.Modules;

[CommandContextType(InteractionContextType.BotDm, InteractionContextType.PrivateChannel, InteractionContextType.Guild)]
[IntegrationType(ApplicationIntegrationType.UserInstall, ApplicationIntegrationType.GuildInstall)]
[Ratelimit(2, 20)]
[Group("ocr", "OCR commands.")]
public class OcrModule : InteractionModuleBase
{
    private readonly ILogger<OcrModule> _logger;
    private readonly IFergunLocalizer<OcrModule> _localizer;
    private readonly SharedModule _shared;
    private readonly InteractiveService _interactive;
    private readonly IGoogleLensClient _googleLens;
    private readonly IBingVisualSearch _bingVisualSearch;
    private readonly IYandexImageSearch _yandexImageSearch;

    public OcrModule(ILogger<OcrModule> logger, IFergunLocalizer<OcrModule> localizer, SharedModule shared, InteractiveService interactive,
        IGoogleLensClient googleLens, IBingVisualSearch bingVisualSearch, IYandexImageSearch yandexImageSearch)
    {
        _logger = logger;
        _localizer = localizer;
        _shared = shared;
        _interactive = interactive;
        _googleLens = googleLens;
        _bingVisualSearch = bingVisualSearch;
        _yandexImageSearch = yandexImageSearch;
    }

    public override void BeforeExecute(ICommandInfo command) => _localizer.CurrentCulture = CultureInfo.GetCultureInfo(Context.Interaction.GetLanguageCode());

    [SlashCommand("bing", "Performs OCR to an image using Bing Visual Search.")]
    public async Task<RuntimeResult> BingAsync([Summary(description: "The URL of an image.")] string? url = null,
        [Summary(description: "An image file.")] IAttachment? file = null)
        => await OcrAsync(OcrEngine.Bing, file?.Url ?? url, Context.Interaction);

    [SlashCommand("google", "Performs OCR to an image using Google Lens.")]
    public async Task<RuntimeResult> GoogleAsync([Summary(description: "The URL of an image.")] string? url = null,
        [Summary(description: "An image file.")] IAttachment? file = null)
        => await OcrAsync(OcrEngine.Google, file?.Url ?? url, Context.Interaction);

    [SlashCommand("yandex", "Performs OCR to an image using Yandex.")]
    public async Task<RuntimeResult> YandexAsync([Summary(description: "The URL of an image.")] string? url = null,
        [Summary(description: "An image file.")] IAttachment? file = null)
        => await OcrAsync(OcrEngine.Yandex, file?.Url ?? url, Context.Interaction);

    [MessageCommand("OCR")]
    public async Task<RuntimeResult> OcrAsync(IMessage message)
    {
        var attachment = message.Attachments.FirstOrDefault();
        var embed = message.Embeds.FirstOrDefault(x => x.Image is not null || x.Thumbnail is not null);

        string? url = attachment?.Url ?? embed?.Image?.Url ?? embed?.Thumbnail?.Url;

        if (url is null)
        {
            return FergunResult.FromError(_localizer["NoImageUrlInMessage"], true);
        }

        var page = new PageBuilder()
            .WithTitle(_localizer["SelectOCREngine"])
            .WithColor(Color.Orange);

        var selection = new SelectionBuilder<OcrEngine>()
            .AddUser(Context.User)
            .WithOptions(Enum.GetValues<OcrEngine>())
            .WithSelectionPage(page)
            .Build();

        var result = await _interactive.SendSelectionAsync(selection, Context.Interaction, TimeSpan.FromMinutes(1), ephemeral: true);

        if (result.IsSuccess)
        {
            return await OcrAsync(result.Value, url, result.StopInteraction!, Context.Interaction, true);
        }

        // Attempt to disable the components
        _ = Context.Interaction.ModifyOriginalResponseAsync(x => x.Components = selection.GetOrAddComponents(true).Build());

        return FergunResult.FromSilentError();
    }

    public async Task<RuntimeResult> OcrAsync(OcrEngine ocrEngine, string? url, IDiscordInteraction interaction,
        IDiscordInteraction? originalInteraction = null, bool ephemeral = false)
    {
        if (url is null)
        {
            return FergunResult.FromError(_localizer["UrlOrAttachmentRequired"], true, interaction);
        }

        if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
        {
            return FergunResult.FromError(_localizer["UrlNotWellFormed"], true, interaction);
        }

        if (!Enum.IsDefined(ocrEngine))
        {
            throw new ArgumentException(_localizer["InvalidOCREngine"], nameof(ocrEngine));
        }

        _logger.LogInformation("Sending OCR request (engine: {Engine}, URL: {Url})", ocrEngine, url);

        if (interaction is IComponentInteraction componentInteraction)
        {
            await componentInteraction.DeferLoadingAsync(ephemeral);
        }
        else
        {
            await interaction.DeferAsync(ephemeral);
        }

        try
        {
            if (originalInteraction is not null)
            {
                _logger.LogDebug("Deleting original interaction response");
                await originalInteraction.DeleteOriginalResponseAsync();
            }
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Failed to delete the original interaction response");
        }

        var stopwatch = Stopwatch.StartNew();
        string? text;

        try
        {
            text = ocrEngine switch
            {
                OcrEngine.Google => await _googleLens.OcrAsync(url),
                OcrEngine.Bing => await _bingVisualSearch.OcrAsync(url),
                OcrEngine.Yandex => await _yandexImageSearch.OcrAsync(url),
                _ => throw new ArgumentException(_localizer["InvalidOCREngine"], nameof(ocrEngine))
            };
        }
        catch (GoogleLensException e)
        {
            _logger.LogWarning(e, "Failed to perform Google Lens OCR to url {Url}", url);
            return FergunResult.FromError(_localizer["GoogleLensOCRError"], ephemeral, interaction);
        }
        catch (BingException e)
        {
            _logger.LogWarning(e, "Failed to perform Bing OCR to url {Url}", url);
            return FergunResult.FromError(e.ImageCategory is null ? e.Message : _localizer[$"Bing{e.ImageCategory}"], ephemeral, interaction);
        }
        catch (YandexException e)
        {
            _logger.LogWarning(e, "Failed to perform Yandex OCR to url {Url}", url);
            return FergunResult.FromError(_localizer["YandexOCRError"], ephemeral, interaction);
        }

        stopwatch.Stop();
        _logger.LogDebug("Received OCR result after {Elapsed}ms", stopwatch.ElapsedMilliseconds);

        if (string.IsNullOrWhiteSpace(text))
        {
            return FergunResult.FromError(_localizer["OCRNoResults"], ephemeral, interaction);
        }

        interaction.TryGetLanguage(out var language);

        (var name, string iconUrl) = ocrEngine switch
        {
            OcrEngine.Google => (_localizer["GoogleLensOCR"], Constants.GoogleLensLogoUrl),
            OcrEngine.Bing => (_localizer["BingVisualSearch"], Constants.BingIconUrl),
            OcrEngine.Yandex => (_localizer["YandexOCR"], Constants.YandexIconUrl),
            _ => throw new ArgumentException(_localizer["InvalidOCREngine"], nameof(ocrEngine))
        };

        string embedText = $"**{_localizer["Output"]}**";

        var builder = new EmbedBuilder()
            .WithTitle(_localizer["OCRResults"])
            .WithDescription($"{embedText}```\n{text.Replace('`', '´').Truncate(EmbedBuilder.MaxDescriptionLength - embedText.Length - 7)}```")
            .WithThumbnailUrl(url)
            .WithFooter(_localizer["OCRFooter", name, stopwatch.ElapsedMilliseconds], iconUrl)
            .WithColor(Color.Orange);

        string buttonText;
        if (language is null)
        {
            _logger.LogDebug("Unable to get GTranslate language from user locale \"{Locale}\"", interaction.GetLocale());
            buttonText = _localizer["Translate"];
        }
        else
        {
            _logger.LogDebug("Retrieved GTranslate language \"{Name}\" from code {Code}", language.Name, language.ISO6391);

            var localizedString = _localizer["TranslateTo", language.NativeName];
            if (localizedString.ResourceNotFound && language.ISO6391 != "en")
            {
                localizedString = _localizer["TranslateToWithNativeName", language.Name, language.NativeName];
            }

            buttonText = localizedString.Value;
        }

        var components = new ComponentBuilder()
            .WithButton(buttonText, "ocrtranslate", ButtonStyle.Secondary)
            .WithButton("TTS", "ocrtts", ButtonStyle.Secondary)
            .Build();

        await interaction.FollowupAsync(embed: builder.Build(), components: components, ephemeral: ephemeral);

        return FergunResult.FromSuccess();
    }

    // Note: Components interactions share the same ratelimit, probably a bug
    [ComponentInteraction("ocrtranslate", true)]
    public async Task<RuntimeResult> OcrTranslateAsync()
    {
        _logger.LogInformation("Received translate request from OCR embed button");

        var embed = ((IComponentInteraction)Context.Interaction).Message.Embeds.FirstOrDefault();
        if (embed is null)
        {
            return FergunResult.FromError(_localizer["EmbedNotFound"], true);
        }

        string text = embed.Description;
        int startIndex = text.IndexOf('`', StringComparison.Ordinal) + 4;
        text = text[startIndex..^3];

        return await _shared.TranslateAsync(Context.Interaction, text, Context.Interaction.GetLanguageCode(), ephemeral: true);
    }

    [ComponentInteraction("ocrtts", true)]
    public async Task<RuntimeResult> OcrTtsAsync()
    {
        _logger.LogInformation("Received TTS request from OCR embed button");

        var embed = ((IComponentInteraction)Context.Interaction).Message.Embeds.FirstOrDefault();
        if (embed is null)
        {
            return FergunResult.FromError(_localizer["EmbedNotFound"], true);
        }

        string text = embed.Description;
        int startIndex = text.IndexOf('`', StringComparison.Ordinal) + 4;
        text = text[startIndex..^3];

        return await _shared.GoogleTtsAsync(Context.Interaction, text, Context.Interaction.GetLanguageCode(), true);
    }
}