using System;

using nietras.SeparatedValues;

namespace ReadDBC_CSV;

internal static class SepHeaderExtensions
{
    public static int IndexOf(this SepHeader sep, string key1, string key2)
    {
        try
        {
            return sep.IndexOf(key1);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());

            return sep.IndexOf(key2);
        }
    }

    public static int IndexOf(this SepHeader sep, string key, int index)
    {
        try
        {
            return sep.IndexOf(key);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.ToString());

            return sep.IndexOf(sep.ColNames[index]);
        }
    }
}
