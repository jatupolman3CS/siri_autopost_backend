using System.Globalization;

namespace SIRIAUTOPOST.Application.Common;

public static class Money
{
    /// <summary>Baht as the activity log and the UI's plain text show them: "790", "257.53".</summary>
    public static string Text(decimal baht) => baht.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Stripe's smallest unit: baht to satang.</summary>
    public static long Satang(decimal baht) => (long)Math.Round(baht * 100, MidpointRounding.AwayFromZero);

    public static decimal FromSatang(long satang) => satang / 100m;
}
