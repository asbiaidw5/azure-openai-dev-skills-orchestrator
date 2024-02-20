using Microsoft.AI.DevTeam.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AI.OpenAI;
using Microsoft.SemanticKernel.Memory;
using Microsoft.SemanticKernel.Orchestration;
using Orleans.Runtime;
using Orleans.Streams;
using System.Text.Json;

namespace Microsoft.AI.DevTeam;
[ImplicitStreamSubscription("DevPersonas")]
public class DeveloperLead : SemanticPersona
{
    private readonly IKernel _kernel;
    private readonly ISemanticTextMemory _memory;
    private readonly ILogger<DeveloperLead> _logger;

    private readonly IManageGithub _ghService;

    protected override string MemorySegment => "dev-lead-memory";

    public DeveloperLead([PersistentState("state", "messages")] IPersistentState<SemanticPersonaState> state, IKernel kernel, ISemanticTextMemory memory, ILogger<DeveloperLead> logger, IManageGithub ghService) : base(state)
    {
        _kernel = kernel;
        _memory = memory;
        _logger = logger;
        _ghService = ghService;
    }
    public async override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        var streamProvider = this.GetStreamProvider("StreamProvider");
        var streamId = StreamId.Create("DevPersonas", this.GetPrimaryKeyString());
        var stream = streamProvider.GetStream<Event>(streamId);

        await stream.SubscribeAsync(HandleEvent);
    }

    public async Task CreateIssue(string org, string repo, long parentNumber, string input)
    {
        var devLeadIssue = await _ghService.CreateIssue(new CreateIssueRequest
        {
            Label = $"{nameof(DevLead)}.{nameof(DevLead.Plan)}",
            Org = org,
            Repo = repo,
            Input = input,
            ParentNumber = parentNumber
        });
        
         _state.State.ParentIssueNumber = parentNumber;
        await _state.WriteStateAsync();
    }
    public async Task<string> CreatePlan(string ask)
    {
        try
        {
            var function = _kernel.CreateSemanticFunction(DevLead.Plan, new OpenAIRequestSettings { MaxTokens = 15000, Temperature = 0.4, TopP = 1 });
            var context = new ContextVariables();
            context.Set("input", ask);
            if (_state.State.History == null) _state.State.History = new List<ChatHistoryItem>();
            _state.State.History.Add(new ChatHistoryItem
            {
                Message = ask,
                Order = _state.State.History.Count + 1,
                UserType = ChatUserType.User
            });
            await AddWafContext(_memory, ask, context);
            context.Set("input", ask);

            var result = await _kernel.RunAsync(context, function);
            var resultMessage = result.ToString();
            _state.State.History.Add(new ChatHistoryItem
            {
                Message = resultMessage,
                Order = _state.State.History.Count + 1,
                UserType = ChatUserType.Agent
            });
            await _state.WriteStateAsync();

            return resultMessage;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating development plan");
            return default;
        }
    }

    public async Task ClosePlan()
    {
        //   var devLead = _grains.GetGrain<ILeadDevelopment>(issueNumber, suffix);
        // var lookup = _grains.GetGrain<ILookupMetadata>(suffix);
        // var parentIssue = await lookup.GetMetadata((int)issueNumber);
        // var conductor = _grains.GetGrain<IOrchestrateWorkflows>(parentIssue.IssueNumber, suffix);
        // var plan = await devLead.GetLatestPlan();
        // await conductor.ImplementationFlow(plan, org, repo, parentIssue.IssueNumber);

        // await _ghService.MarkTaskComplete(new MarkTaskCompleteRequest
        // {
        //     Org = org,
        //     Repo = repo,
        //     CommentId = parentIssue.CommentId
        // });
    }

    public Task<DevLeadPlanResponse> GetLatestPlan()
    {
        var plan = _state.State.History.Last().Message;
        var response = JsonSerializer.Deserialize<DevLeadPlanResponse>(plan);
        return Task.FromResult(response);
    }

    public async override Task HandleEvent(Event item, StreamSequenceToken? token)
    {
        switch (item.Type)
        {
            case EventType.NewAsk:
                await CreateIssue(item.Data["org"],  item.Data["repo"], long.Parse(item.Data["issueNumber"]) , item.Message);
                break;
            case EventType.NewAskPlan:
                await CreatePlan(item.Message);
                break;
            case EventType.ChainClosed:
                await ClosePlan();
                break;
            default:
                break;
        }
    }
}

[GenerateSerializer]
public class DevLeadPlanResponse
{
    [Id(0)]
    public List<Step> steps { get; set; }
}

[GenerateSerializer]
public class Step
{
    [Id(0)]
    public string description { get; set; }
    [Id(1)]
    public string step { get; set; }
    [Id(2)]
    public List<Subtask> subtasks { get; set; }
}

[GenerateSerializer]
public class Subtask
{
    [Id(0)]
    public string subtask { get; set; }
    [Id(1)]
    public string prompt { get; set; }
}




