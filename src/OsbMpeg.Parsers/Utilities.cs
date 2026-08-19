namespace OsbMpeg.Parsers;

public static class Utilities
{
    public static bool IsEqual(this double a, double b, double epsilon = 1e-9)
    {
        return Math.Abs(a - b) <= epsilon;
    }
    
    public static bool IsEqual(this float a, float b, float epsilon = 1e-6f)
    {
        return Math.Abs(a - b) <= epsilon;
    }
}