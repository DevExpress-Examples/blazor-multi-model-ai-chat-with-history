using Microsoft.Extensions.AI;

namespace DXBlazorChatSelector.Services;

public class CompositeChatClient : IChatClient
{
    private readonly IChatThreadStore _threadStore;
    private readonly IChatThreadTitleGenerator _titleGenerator;
    private readonly object _syncRoot = new();
    private readonly HashSet<Guid> _titleGenerationInProgress = new();
    private Guid? _activeThreadId;

    public List<ChatClientSession> AvailableChatClients { get; }
    public ChatClientSession? SelectedSession { get; set; }
    public event Action<Guid, string>? ThreadTitleUpdated;

    public CompositeChatClient(IChatThreadStore threadStore, IChatThreadTitleGenerator titleGenerator, params ChatClientSession[] chatClients)
    {
        _threadStore = threadStore ?? throw new ArgumentNullException(nameof(threadStore));
        _titleGenerator = titleGenerator ?? throw new ArgumentNullException(nameof(titleGenerator));

        if (chatClients is null || chatClients.Length == 0)
        {
            throw new ArgumentException("At least one chat client session must be provided.", nameof(chatClients));
        }

        AvailableChatClients = chatClients.ToList();
        SelectedSession = AvailableChatClients[0];
    }

    public ChatClientSession GetRequiredSelectedSession()
    {
        return SelectedSession ?? throw new InvalidOperationException("No model session is selected.");
    }

    public void SetActiveThread(Guid? threadId)
    {
        _activeThreadId = threadId;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        var selectedSession = GetRequiredSelectedSession();
        TryQueueTitleGeneration(messages, selectedSession);
        return selectedSession.Client.GetResponseAsync(messages, options, cancellationToken);
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = new CancellationToken()) {
        var selectedSession = GetRequiredSelectedSession();
        TryQueueTitleGeneration(messages, selectedSession);
        return selectedSession.Client.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    public void Dispose() {
        foreach (var session in AvailableChatClients)
        {
            session.Client.Dispose();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) {
        return GetRequiredSelectedSession().Client.GetService(serviceType, serviceKey);
    }

    private void TryQueueTitleGeneration(IEnumerable<ChatMessage> messages, ChatClientSession selectedSession)
    {
        if (_activeThreadId is null)
        {
            return;
        }

        var threadId = _activeThreadId.Value;
        var firstUserMessage = GetFirstUserMessage(messages);
        if (string.IsNullOrWhiteSpace(firstUserMessage))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_titleGenerationInProgress.Contains(threadId))
            {
                return;
            }
            _titleGenerationInProgress.Add(threadId);
        }

        _ = GenerateTitleForThreadAsync(threadId, selectedSession, firstUserMessage);
    }

    private async Task GenerateTitleForThreadAsync(Guid threadId, ChatClientSession selectedSession, string firstUserMessage)
    {
        try
        {
            var thread = await _threadStore.GetThreadAsync(threadId, CancellationToken.None);
            if (thread is null || thread.HasGeneratedTitle)
            {
                return;
            }

            var modelSession = AvailableChatClients.FirstOrDefault(x => x.Id == thread.ModelSessionId) ?? selectedSession;

            string generatedTitle;
            try
            {
                generatedTitle = await _titleGenerator.GenerateTitleAsync(modelSession, firstUserMessage, CancellationToken.None);
            }
            catch
            {
                generatedTitle = _titleGenerator.BuildFallbackTitle(firstUserMessage);
            }

            if (string.IsNullOrWhiteSpace(generatedTitle))
            {
                generatedTitle = _titleGenerator.BuildFallbackTitle(firstUserMessage);
            }

            await _threadStore.UpdateTitleAsync(threadId, generatedTitle, true, CancellationToken.None);
            ThreadTitleUpdated?.Invoke(threadId, generatedTitle);
        }
        finally
        {
            lock (_syncRoot)
            {
                _titleGenerationInProgress.Remove(threadId);
            }
        }
    }

    private static string? GetFirstUserMessage(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
            {
                return message.Text;
            }
        }

        return null;
    }
}