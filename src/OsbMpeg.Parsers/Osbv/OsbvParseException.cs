namespace OsbMpeg.Osbv;

public sealed class OsbvParseException(int line, string message) : Exception($"line {line}: {message}")
{
    public int Line { get; } = line;
}
