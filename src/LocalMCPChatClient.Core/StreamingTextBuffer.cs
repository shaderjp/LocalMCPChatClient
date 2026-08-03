using System.Text;

namespace LocalMCPChatClient.Core;

public sealed class StreamingTextBuffer(int minimumUpdateMilliseconds = 50)
{
    private readonly StringBuilder _builder = new();
    private bool _hasPublished;

    public int Length => _builder.Length;
    public string Content => _builder.ToString();

    public bool Append(string? delta, long millisecondsSinceLastUpdate)
    {
        if (!string.IsNullOrEmpty(delta)) _builder.Append(delta);
        if (_builder.Length == 0) return false;
        if (!_hasPublished || millisecondsSinceLastUpdate >= minimumUpdateMilliseconds)
        {
            _hasPublished = true;
            return true;
        }
        return false;
    }

    public StreamingTextBuffer AppendLine()
    {
        _builder.AppendLine();
        return this;
    }

    public StreamingTextBuffer Append(string value)
    {
        _builder.Append(value);
        return this;
    }
}
