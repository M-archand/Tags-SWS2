using SwiftlyS2.Shared.Misc;
using SwiftlyS2.Shared.Players;
using TagsApi;
using static TagsApi.Tags;

namespace Tags;

public class TagsAPI : ITagApi
{
    private bool _isProcessingTagsUpdatedPre;
    private bool _isProcessingTagsUpdatedPost;
    private bool _isProcessingMessagePre;
    private bool _isProcessingMessage;
    private bool _isProcessingMessagePost;

    public event Func<MessageProcess, HookResult>? OnMessageProcessPre;
    public event Func<MessageProcess, HookResult>? OnMessageProcess;
    public event Action<MessageProcess>? OnMessageProcessPost;
    public event Action<IPlayer, Tag>? OnTagsUpdatedPre;
    public event Action<IPlayer, Tag>? OnTagsUpdatedPost;

    private static HookResult InvokeHook(Func<MessageProcess, HookResult>? eventHandler, MessageProcess data)
    {
        if (eventHandler == null)
            return HookResult.Continue;

        HookResult finalResult = HookResult.Continue;

        foreach (Delegate handler in eventHandler.GetInvocationList())
        {
            if (handler is Func<MessageProcess, HookResult> typedHandler)
            {
                HookResult result = typedHandler(data);

                if (result == HookResult.Stop)
                    return HookResult.Stop;

                if (result == HookResult.Handled)
                    finalResult = HookResult.Handled;
            }
        }

        return finalResult;
    }

    public HookResult MessageProcessPre(MessageProcess messageProcess)
    {
        if (_isProcessingMessagePre)
            return HookResult.Continue;

        _isProcessingMessagePre = true;

        try
        {
            return InvokeHook(OnMessageProcessPre, messageProcess);
        }
        finally
        {
            _isProcessingMessagePre = false;
        }
    }

    public HookResult MessageProcess(MessageProcess messageProcess)
    {
        if (_isProcessingMessage)
            return HookResult.Continue;

        _isProcessingMessage = true;

        try
        {
            return InvokeHook(OnMessageProcess, messageProcess);
        }
        finally
        {
            _isProcessingMessage = false;
        }
    }

    public void MessageProcessPost(MessageProcess messageProcess)
    {
        if (_isProcessingMessagePost)
            return;

        _isProcessingMessagePost = true;

        try
        {
            OnMessageProcessPost?.Invoke(messageProcess);
        }
        finally
        {
            _isProcessingMessagePost = false;
        }
    }

    public void TagsUpdatedPre(IPlayer player, Tag tag)
    {
        if (_isProcessingTagsUpdatedPre)
            return;

        _isProcessingTagsUpdatedPre = true;

        try
        {
            OnTagsUpdatedPre?.Invoke(player, tag);
        }
        finally
        {
            _isProcessingTagsUpdatedPre = false;
        }
    }

    public void TagsUpdatedPost(IPlayer player, Tag tag)
    {
        if (_isProcessingTagsUpdatedPost)
            return;

        _isProcessingTagsUpdatedPost = true;

        try
        {
            OnTagsUpdatedPost?.Invoke(player, tag);
        }
        finally
        {
            _isProcessingTagsUpdatedPost = false;
        }
    }
 
    public void AddAttribute(IPlayer player, TagType types, TagPrePost prePost, string newValue)
    {
        if (player == null || !player.IsValid)
            return;

        player.AddAttribute(types, prePost, newValue);
    }

    public void SetAttribute(IPlayer player, TagType types, string newValue)
    {
        if (player == null || !player.IsValid)
            return;

        player.SetAttribute(types, newValue);
    }

    public string? GetAttribute(IPlayer player, TagType type)
    {
        if (player == null || !player.IsValid)
            return null;

        return player.GetAttribute(type);
    }

    public void ResetAttribute(IPlayer player, TagType types)
    {
        if (player == null || !player.IsValid)
            return;

        player.ResetAttribute(types);
    }

    public bool GetPlayerChatSound(IPlayer player)
    {
        if (player == null || !player.IsValid)
            return true;

        return player.GetChatSound();
    }

    public void SetPlayerChatSound(IPlayer player, bool value)
    {
        if (player == null || !player.IsValid)
            return;

        player.SetChatSound(value);
    }

    public bool GetPlayerVisibility(IPlayer player)
    {
        if (player == null || !player.IsValid)
            return false;

        return player.GetVisibility();
    }

    public void SetPlayerVisibility(IPlayer player, bool value)
    {
        if (player == null || !player.IsValid)
            return;

        player.SetVisibility(value);
    }

    public void ReloadTags()
    {
        TagExtensions.ReloadTags();
    }
}