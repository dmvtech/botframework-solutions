﻿using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CalendarSkill.Models;
using CalendarSkill.Options;
using CalendarSkill.Prompts;
using CalendarSkill.Responses.CreateEvent;
using CalendarSkill.Responses.Shared;
using CalendarSkill.Services;
using CalendarSkill.Utilities;
using Microsoft.Bot.Builder;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.LanguageGeneration;
using Microsoft.Bot.Builder.Skills;
using Microsoft.Bot.Builder.Solutions.Extensions;
using Microsoft.Bot.Builder.Solutions.Resources;
using Microsoft.Bot.Builder.Solutions.Responses;
using Microsoft.Bot.Builder.Solutions.Util;
using Microsoft.Bot.Connector.Authentication;
using Microsoft.Bot.Schema;
using Microsoft.Recognizers.Text.DateTime;
using static CalendarSkill.Models.CreateEventStateModel;

namespace CalendarSkill.Dialogs
{
    public class CreateEventDialog : CalendarSkillDialogBase
    {
        private ResourceMultiLanguageGenerator _lgMultiLangEngine;

        public CreateEventDialog(
            BotSettings settings,
            BotServices services,
            ResponseManager responseManager,
            ConversationState conversationState,
            FindContactDialog findContactDialog,
            IServiceManager serviceManager,
            IBotTelemetryClient telemetryClient,
            MicrosoftAppCredentials appCredentials)
            : base(nameof(CreateEventDialog), settings, services, responseManager, conversationState, serviceManager, telemetryClient, appCredentials)
        {
            TelemetryClient = telemetryClient;
            _lgMultiLangEngine = new ResourceMultiLanguageGenerator("CreateEventDialog.lg");

            var createEvent = new WaterfallStep[]
            {
                GetAuthToken,
                AfterGetAuthToken,
                CollectAttendees,
                CollectTitle,
                CollectContent,
                CollectStartDate,
                CollectStartTime,
                CollectDuration,
                CollectLocation,
                ConfirmBeforeCreate,
                ConfirmBeforeCreatePrompt,
                CreateEvent,
            };

            var updateStartDate = new WaterfallStep[]
            {
                UpdateStartDateForCreate,
                AfterUpdateStartDateForCreate,
            };

            var updateStartTime = new WaterfallStep[]
            {
                UpdateStartTimeForCreate,
                AfterUpdateStartTimeForCreate,
            };

            var updateDuration = new WaterfallStep[]
            {
                UpdateDurationForCreate,
                AfterUpdateDurationForCreate,
            };

            var getRecreateInfo = new WaterfallStep[]
            {
                GetRecreateInfo,
                AfterGetRecreateInfo,
            };

            var showRestParticipants = new WaterfallStep[]
            {
                ShowRestParticipantsPrompt,
                ShowRestParticipants,
            };

            // Define the conversation flow using a waterfall model.
            AddDialog(new WaterfallDialog(Actions.CreateEvent, createEvent) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.UpdateStartDateForCreate, updateStartDate) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.UpdateStartTimeForCreate, updateStartTime) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.UpdateDurationForCreate, updateDuration) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.GetRecreateInfo, getRecreateInfo) { TelemetryClient = telemetryClient });
            AddDialog(new WaterfallDialog(Actions.ShowRestParticipants, showRestParticipants) { TelemetryClient = telemetryClient });
            AddDialog(new DatePrompt(Actions.DatePromptForCreate));
            AddDialog(new TimePrompt(Actions.TimePromptForCreate));
            AddDialog(new DurationPrompt(Actions.DurationPromptForCreate));
            AddDialog(new GetRecreateInfoPrompt(Actions.GetRecreateInfoPrompt));
            AddDialog(findContactDialog ?? throw new ArgumentNullException(nameof(findContactDialog)));

            // Set starting dialog for component
            InitialDialogId = Actions.CreateEvent;
        }

        // Create Event waterfall steps
        public async Task<DialogTurnResult> CollectTitle(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isTitleSkipByDefault = false;
                isTitleSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventTitle")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);

                if (state.RecreateState == RecreateEventState.Subject)
                {
                    return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = ResponseManager.GetResponse(CreateEventResponses.NoTitleShort) }, cancellationToken);
                }
                else if (state.CreateHasDetail && isTitleSkipByDefault.GetValueOrDefault())
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
                else if (string.IsNullOrEmpty(state.Title))
                {
                    if (state.Attendees.Count == 0 || state.Attendees == null)
                    {
                        state.FirstRetryInFindContact = true;
                        state.Clear();
                        return await sc.EndDialogAsync();
                    }

                    var data = new { participants = state.Attendees };
                    var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoTitle]", data);
                    var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);

                    return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = (Activity)prompt }, cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CollectContent(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isTitleSkipByDefault = false;
                isTitleSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventTitle")?.IsSkipByDefault;

                bool? isContentSkipByDefault = false;
                isContentSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventContent")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (sc.Result != null || (state.CreateHasDetail && isTitleSkipByDefault.GetValueOrDefault()) || state.RecreateState == RecreateEventState.Subject)
                {
                    if (string.IsNullOrEmpty(state.Title))
                    {
                        if (state.CreateHasDetail && isTitleSkipByDefault.GetValueOrDefault() && state.RecreateState != RecreateEventState.Subject)
                        {
                            state.Title = CreateEventWhiteList.GetDefaultTitle();
                        }
                        else
                        {
                            sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                            var title = content != null ? content.ToString() : sc.Context.Activity.Text;
                            if (CreateEventWhiteList.IsSkip(title))
                            {
                                state.Title = CreateEventWhiteList.GetDefaultTitle();
                            }
                            else
                            {
                                state.Title = title;
                            }
                        }
                    }
                }

                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoContent]", null);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);

                if (string.IsNullOrEmpty(state.Content) && (!(state.CreateHasDetail && isContentSkipByDefault.GetValueOrDefault()) || state.RecreateState == RecreateEventState.Content))
                {
                    return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = (Activity)prompt }, cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CollectAttendees(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (string.IsNullOrEmpty(state.APIToken))
                {
                    return await sc.EndDialogAsync(true, cancellationToken);
                }

                ServiceManager.InitCalendarService(state.APIToken, state.EventSource);

                if (state.Attendees.Count == 0 || state.RecreateState == RecreateEventState.Participants)
                {
                    return await sc.BeginDialogAsync(nameof(FindContactDialog), options: new FindContactDialogOptions(sc.Options), cancellationToken: cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CollectStartDate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isContentSkipByDefault = false;
                isContentSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventContent")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (sc.Result != null && (!(state.CreateHasDetail && isContentSkipByDefault.GetValueOrDefault()) || state.RecreateState == RecreateEventState.Content))
                {
                    if (string.IsNullOrEmpty(state.Content))
                    {
                        sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                        var merged_content = content != null ? content.ToString() : sc.Context.Activity.Text;
                        if (!CreateEventWhiteList.IsSkip(merged_content))
                        {
                            state.Content = merged_content;
                        }
                    }
                }
                else if (state.CreateHasDetail && isContentSkipByDefault.GetValueOrDefault())
                {
                    state.Content = CalendarCommonStrings.DefaultContent;
                }

                if (!state.StartDate.Any())
                {
                    return await sc.BeginDialogAsync(Actions.UpdateStartDateForCreate, new UpdateDateTimeDialogOptions(UpdateDateTimeDialogOptions.UpdateReason.NotFound), cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CollectStartTime(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (state.RecreateState == null || state.RecreateState == RecreateEventState.Time)
                {
                    return await sc.BeginDialogAsync(Actions.UpdateStartTimeForCreate, new UpdateDateTimeDialogOptions(UpdateDateTimeDialogOptions.UpdateReason.NotFound), cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CollectDuration(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);

                if (state.EndDateTime == null)
                {
                    return await sc.BeginDialogAsync(Actions.UpdateDurationForCreate, new UpdateDateTimeDialogOptions(UpdateDateTimeDialogOptions.UpdateReason.NotFound), cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CollectLocation(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isLocationSkipByDefault = false;
                isLocationSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventLocation")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);

                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoLocation]", null);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);

                if (state.Location == null && (!(state.CreateHasDetail && isLocationSkipByDefault.GetValueOrDefault()) || state.RecreateState == RecreateEventState.Location))
                {
                    return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = (Activity)prompt }, cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> ConfirmBeforeCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isLocationSkipByDefault = false;
                isLocationSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventLocation")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (state.Location == null && sc.Result != null && (!(state.CreateHasDetail && isLocationSkipByDefault.GetValueOrDefault()) || state.RecreateState == RecreateEventState.Location))
                {
                    sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);
                    var luisResult = state.LuisResult;

                    var userInput = content != null ? content.ToString() : sc.Context.Activity.Text;
                    var topIntent = luisResult?.TopIntent().intent.ToString();

                    var promptRecognizerResult = ConfirmRecognizerHelper.ConfirmYesOrNo(userInput, sc.Context.Activity.Locale);

                    // Enable the user to skip providing the location if they say something matching the Cancel intent, say something matching the ConfirmNo recognizer or something matching the NoLocation intent
                    if (CreateEventWhiteList.IsSkip(userInput))
                    {
                        state.Location = string.Empty;
                    }
                    else
                    {
                        state.Location = userInput;
                    }
                }
                else if (state.CreateHasDetail && isLocationSkipByDefault.GetValueOrDefault())
                {
                    state.Location = CalendarCommonStrings.DefaultLocation;
                }

                var source = state.EventSource;
                var newEvent = new EventModel(source)
                {
                    Title = state.Title,
                    Content = state.Content,
                    Attendees = state.Attendees,
                    StartTime = state.StartDateTime.Value,
                    EndTime = state.EndDateTime.Value,
                    TimeZone = TimeZoneInfo.Utc,
                    Location = state.Location,
                    ContentPreview = state.Content
                };

                var attendeePhotoList = new List<string>();

                foreach (var attendee in dialogState.FindContactInfor.Contacts)
                {
                    attendeePhotoList.Add(await GetUserPhotoUrlAsync(sc.Context, attendee));
                }

                var data = new
                {
                    startDateTime = dialogState.StartDateTime.Value,
                    endDateTime = dialogState.EndDateTime.Value,
                    timezone = userState.GetUserTimeZone().Id,
                    attendees = dialogState.FindContactInfor.Contacts,
                    attendeePhotoList,
                    subject = dialogState.Title,
                    location = dialogState.Location,
                    content = dialogState.Content
                };

                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[ConfirmCreate]", data);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);

                await sc.Context.SendActivityAsync(prompt);

                if (state.Attendees.Count > 5)
                {
                    return await sc.BeginDialogAsync(Actions.ShowRestParticipants);
                }
                else
                {
                    return await sc.NextAsync();
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> ConfirmBeforeCreatePrompt(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[ConfirmCreatePrompt]", null);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);
                var rePromptLGResult = await _lgMultiLangEngine.Generate(sc.Context, "[ConfirmCreateFailed]", null);
                var retryPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, rePromptLGResult, null);

                return await sc.PromptAsync(Actions.TakeFurtherAction, new PromptOptions
                {
                    Prompt = (Activity)prompt,
                    RetryPrompt = (Activity)retryPrompt
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> CreateEvent(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                var confirmResult = (bool)sc.Result;
                if (confirmResult)
                {
                    var source = state.EventSource;
                    var newEvent = new EventModel(source)
                    {
                        Title = state.Title,
                        Content = state.Content,
                        Attendees = state.Attendees,
                        StartTime = (DateTime)state.StartDateTime,
                        EndTime = (DateTime)state.EndDateTime,
                        TimeZone = TimeZoneInfo.Utc,
                        Location = state.Location,
                    };

                    var calendarService = ServiceManager.InitCalendarService(state.APIToken, state.EventSource);
                    if (await calendarService.CreateEvent(newEvent) != null)
                    {
                        var attendeePhotoList = new List<string>();

                        foreach (var attendee in dialogState.FindContactInfor.Contacts)
                        {
                            attendeePhotoList.Add(await GetUserPhotoUrlAsync(sc.Context, attendee));
                        }

                        var data = new
                        {
                            startDateTime = dialogState.StartDateTime.Value,
                            endDateTime = dialogState.EndDateTime.Value,
                            timezone = userState.GetUserTimeZone().Id,
                            attendees = dialogState.FindContactInfor.Contacts,
                            attendeePhotoList,
                            subject = dialogState.Title,
                            location = dialogState.Location,
                            content = dialogState.Content
                        };

                        var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[EventCreated]", data);
                        var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);

                        newEvent.ContentPreview = dialogState.Content;

                        await sc.Context.SendActivityAsync(prompt, cancellationToken);
                    }
                    else
                    {
                        var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[EventCreationFailed]", null);
                        var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);
                        return await sc.PromptAsync(Actions.Prompt, new PromptOptions { Prompt = (Activity)prompt }, cancellationToken);
                    }

                    state.Clear();
                }
                else
                {
                    return await sc.ReplaceDialogAsync(Actions.GetRecreateInfo, options: sc.Options, cancellationToken: cancellationToken);
                }

                return await sc.EndDialogAsync(true, cancellationToken);
            }
            catch (SkillException ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        // update start date waterfall steps
        public async Task<DialogTurnResult> UpdateStartDateForCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isStartDateSkipByDefault = false;
                isStartDateSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventStartDate")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (state.CreateHasDetail && isStartDateSkipByDefault.GetValueOrDefault() && state.RecreateState != RecreateEventState.Time)
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }

                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoStartDate]", null);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);
                var retryLGResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoStartDateRetry]", null);
                var retryPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, retryLGResult, null);

                return await sc.PromptAsync(Actions.DatePromptForCreate, new PromptOptions
                {
                    Prompt = (Activity)prompt,
                    RetryPrompt = (Activity)retryPrompt,
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> AfterUpdateStartDateForCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isStartDateSkipByDefault = false;
                isStartDateSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventStartDate")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (state.CreateHasDetail && isStartDateSkipByDefault.GetValueOrDefault() && state.RecreateState != RecreateEventState.Time)
                {
                    var datetime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, state.GetUserTimeZone());
                    var defaultValue = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventStartDate")?.DefaultValue;
                    if (int.TryParse(defaultValue, out var startDateOffset))
                    {
                        datetime = datetime.AddDays(startDateOffset);
                    }

                    state.StartDate.Add(datetime);
                }
                else
                if (sc.Result != null)
                {
                    IList<DateTimeResolution> dateTimeResolutions = sc.Result as List<DateTimeResolution>;
                    foreach (var resolution in dateTimeResolutions)
                    {
                        var dateTimeConvertType = resolution?.Timex;
                        var dateTimeValue = resolution?.Value;
                        if (dateTimeValue != null)
                        {
                            try
                            {
                                var dateTime = DateTime.Parse(dateTimeValue);

                                if (dateTime != null)
                                {
                                    var isRelativeTime = IsRelativeTime(sc.Context.Activity.Text, dateTimeValue, dateTimeConvertType);
                                    if (ContainsTime(dateTimeConvertType))
                                    {
                                        state.StartTime.Add(TimeZoneInfo.ConvertTime(dateTime, TimeZoneInfo.Local, state.GetUserTimeZone()));
                                    }

                                    // Workaround as DateTimePrompt only return as local time
                                    if (isRelativeTime)
                                    {
                                        dateTime = new DateTime(
                                            dateTime.Year,
                                            dateTime.Month,
                                            dateTime.Day,
                                            DateTime.Now.Hour,
                                            DateTime.Now.Minute,
                                            DateTime.Now.Second);
                                    }

                                    state.StartDate.Add(isRelativeTime ? TimeZoneInfo.ConvertTime(dateTime, TimeZoneInfo.Local, state.GetUserTimeZone()) : dateTime);
                                }
                            }
                            catch (FormatException ex)
                            {
                                await HandleExpectedDialogExceptions(sc, ex);
                            }
                        }
                    }
                }

                return await sc.EndDialogAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        // update start time waterfall steps
        public async Task<DialogTurnResult> UpdateStartTimeForCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (!state.StartTime.Any())
                {
                    var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoStartTime]", null);
                    var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);
                    var retryLGResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoStartTimeRetry]", null);
                    var retryPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, retryLGResult, null);
                    var noSkipLGResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoStartTimeNoSkip]", null);
                    var noSkipPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, noSkipLGResult, null);

                    return await sc.PromptAsync(Actions.TimePromptForCreate, new NoSkipPromptOptions
                    {
                        Prompt = (Activity)prompt,
                        RetryPrompt = (Activity)retryPrompt,
                        NoSkipPrompt = (Activity)noSkipPrompt
                    }, cancellationToken);
                }
                else
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> AfterUpdateStartTimeForCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (sc.Result != null && !state.StartTime.Any())
                {
                    IList<DateTimeResolution> dateTimeResolutions = sc.Result as List<DateTimeResolution>;
                    foreach (var resolution in dateTimeResolutions)
                    {
                        var dateTimeConvertType = resolution?.Timex;
                        var dateTimeValue = resolution?.Value;
                        if (dateTimeValue != null)
                        {
                            try
                            {
                                var dateTime = DateTime.Parse(dateTimeValue);

                                if (dateTime != null)
                                {
                                    var isRelativeTime = IsRelativeTime(sc.Context.Activity.Text, dateTimeValue, dateTimeConvertType);
                                    state.StartTime.Add(isRelativeTime ? TimeZoneInfo.ConvertTime(dateTime, TimeZoneInfo.Local, state.GetUserTimeZone()) : dateTime);
                                }
                            }
                            catch (FormatException ex)
                            {
                                await HandleExpectedDialogExceptions(sc, ex);
                            }
                        }
                    }
                }

                var userNow = TimeConverter.ConvertUtcToUserTime(DateTime.UtcNow, state.GetUserTimeZone());
                var startDate = state.StartDate.Last();
                foreach (var startTime in state.StartTime)
                {
                    var startDateTime = new DateTime(
                        startDate.Year,
                        startDate.Month,
                        startDate.Day,
                        startTime.Hour,
                        startTime.Minute,
                        startTime.Second);
                    if (state.StartDateTime == null)
                    {
                        state.StartDateTime = startDateTime;
                    }

                    if (startDateTime >= userNow)
                    {
                        state.StartDateTime = startDateTime;
                        break;
                    }
                }

                state.StartDateTime = TimeZoneInfo.ConvertTimeToUtc(state.StartDateTime.Value, state.GetUserTimeZone());
                return await sc.EndDialogAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        // update duration waterfall steps
        public async Task<DialogTurnResult> UpdateDurationForCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isDurationSkipByDefault = false;
                isDurationSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventDuration")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (state.Duration > 0 || state.EndTime.Any() || state.EndDate.Any() || (state.CreateHasDetail && isDurationSkipByDefault.GetValueOrDefault() && state.RecreateState != RecreateEventState.Time && state.RecreateState != RecreateEventState.Duration))
                {
                    return await sc.NextAsync(cancellationToken: cancellationToken);
                }

                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoDuration]", null);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);
                var retryLGResult = await _lgMultiLangEngine.Generate(sc.Context, "[NoDurationRetry]", null);
                var retryPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, retryLGResult, null);

                return await sc.PromptAsync(Actions.DurationPromptForCreate, new PromptOptions
                {
                    Prompt = (Activity)prompt,
                    RetryPrompt = (Activity)retryPrompt
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> AfterUpdateDurationForCreate(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                bool? isDurationSkipByDefault = false;
                isDurationSkipByDefault = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventDuration")?.IsSkipByDefault;

                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (state.EndDate.Any() || state.EndTime.Any())
                {
                    var startDate = !state.StartDate.Any() ? TimeConverter.ConvertUtcToUserTime(DateTime.UtcNow, state.GetUserTimeZone()) : state.StartDate.Last();
                    var endDate = startDate;
                    if (state.EndDate.Any())
                    {
                        endDate = state.EndDate.Last();
                    }

                    if (state.EndTime.Any())
                    {
                        foreach (var endtime in state.EndTime)
                        {
                            var endDateTime = new DateTime(
                                endDate.Year,
                                endDate.Month,
                                endDate.Day,
                                endtime.Hour,
                                endtime.Minute,
                                endtime.Second);
                            endDateTime = TimeZoneInfo.ConvertTimeToUtc(endDateTime, state.GetUserTimeZone());
                            if (state.EndDateTime == null || endDateTime >= state.StartDateTime)
                            {
                                state.EndDateTime = endDateTime;
                            }
                        }
                    }
                    else
                    {
                        state.EndDateTime = new DateTime(endDate.Year, endDate.Month, endDate.Day, 23, 59, 59);
                        state.EndDateTime = TimeZoneInfo.ConvertTimeToUtc(state.EndDateTime.Value, state.GetUserTimeZone());
                    }

                    var ts = state.StartDateTime.Value.Subtract(state.EndDateTime.Value).Duration();
                    state.Duration = (int)ts.TotalSeconds;
                }

                if (state.Duration <= 0 && state.CreateHasDetail && isDurationSkipByDefault.GetValueOrDefault() && state.RecreateState != RecreateEventState.Time && state.RecreateState != RecreateEventState.Duration)
                {
                    var defaultValue = Settings.DefaultValue?.CreateMeeting?.First(item => item.Name == "EventDuration")?.DefaultValue;
                    if (int.TryParse(defaultValue, out var durationMinutes))
                    {
                        state.Duration = durationMinutes * 60;
                    }
                    else
                    {
                        state.Duration = 1800;
                    }
                }

                if (state.Duration <= 0 && sc.Result != null)
                {
                    sc.Context.Activity.Properties.TryGetValue("OriginText", out var content);

                    IList<DateTimeResolution> dateTimeResolutions = sc.Result as List<DateTimeResolution>;
                    if (dateTimeResolutions.First().Value != null)
                    {
                        int.TryParse(dateTimeResolutions.First().Value, out var duration);
                        state.Duration = duration;
                    }
                }

                if (state.Duration > 0)
                {
                    state.EndDateTime = state.StartDateTime.Value.AddSeconds(state.Duration);
                }
                else
                {
                    // should not go to this part in current logic.
                    // place an error handling for save.
                    await HandleDialogExceptions(sc, new Exception("Unexpect Error On get duration"));
                }

                return await sc.EndDialogAsync(cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> GetRecreateInfo(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var lgResult = await _lgMultiLangEngine.Generate(sc.Context, "[GetRecreateInfo]", null);
                var prompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, lgResult, null);
                var retryLGResult = await _lgMultiLangEngine.Generate(sc.Context, "[GetRecreateInfoRetry]", null);
                var retryPrompt = await new TextMessageActivityGenerator().CreateActivityFromText(sc.Context, retryLGResult, null);

                return await sc.PromptAsync(Actions.GetRecreateInfoPrompt, new PromptOptions
                {
                    Prompt = (Activity)prompt,
                    RetryPrompt = (Activity)retryPrompt
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> AfterGetRecreateInfo(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                if (sc.Result != null)
                {
                    var recreateState = sc.Result as RecreateEventState?;
                    switch (recreateState.Value)
                    {
                        case RecreateEventState.Cancel:
                            await sc.Context.SendActivityAsync(ResponseManager.GetResponse(CalendarSharedResponses.ActionEnded), cancellationToken);
                            state.Clear();
                            return await sc.EndDialogAsync(true, cancellationToken);
                        case RecreateEventState.Time:
                            state.ClearTimes();
                            return await sc.ReplaceDialogAsync(Actions.CreateEvent, options: sc.Options, cancellationToken: cancellationToken);
                        case RecreateEventState.Duration:
                            state.ClearTimesExceptStartTime();
                            return await sc.ReplaceDialogAsync(Actions.CreateEvent, options: sc.Options, cancellationToken: cancellationToken);
                        case RecreateEventState.Location:
                            state.ClearLocation();
                            return await sc.ReplaceDialogAsync(Actions.CreateEvent, options: sc.Options, cancellationToken: cancellationToken);
                        case RecreateEventState.Participants:
                            state.ClearParticipants();
                            return await sc.ReplaceDialogAsync(Actions.CreateEvent, options: sc.Options, cancellationToken: cancellationToken);
                        case RecreateEventState.Subject:
                            state.ClearSubject();
                            return await sc.ReplaceDialogAsync(Actions.CreateEvent, options: sc.Options, cancellationToken: cancellationToken);
                        case RecreateEventState.Content:
                            state.ClearContent();
                            return await sc.ReplaceDialogAsync(Actions.CreateEvent, options: sc.Options, cancellationToken: cancellationToken);
                        default:
                            // should not go to this part. place an error handling for save.
                            await HandleDialogExceptions(sc, new Exception("Get unexpect state in recreate."));
                            return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
                    }
                }
                else
                {
                    // should not go to this part. place an error handling for save.
                    await HandleDialogExceptions(sc, new Exception("Get unexpect result in recreate."));
                    return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
                }
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);
                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> ShowRestParticipantsPrompt(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                return await sc.PromptAsync(Actions.TakeFurtherAction, new PromptOptions
                {
                    Prompt = ResponseManager.GetResponse(CreateEventResponses.ShowRestParticipantsPrompt),
                    RetryPrompt = ResponseManager.GetResponse(CreateEventResponses.ShowRestParticipantsPrompt)
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }

        public async Task<DialogTurnResult> ShowRestParticipants(WaterfallStepContext sc, CancellationToken cancellationToken = default(CancellationToken))
        {
            try
            {
                var state = await Accessor.GetAsync(sc.Context, cancellationToken: cancellationToken);
                var confirmResult = (bool)sc.Result;
                if (confirmResult)
                {
                    await sc.Context.SendActivityAsync(state.Attendees.GetRange(5, state.Attendees.Count - 5).ToSpeechString(CommonStrings.And, li => li.DisplayName ?? li.Address));
                }

                return await sc.EndDialogAsync(true, cancellationToken);
            }
            catch (Exception ex)
            {
                await HandleDialogExceptions(sc, ex);

                return new DialogTurnResult(DialogTurnStatus.Cancelled, CommonUtil.DialogTurnResultCancelAllDialogs);
            }
        }
    }
}