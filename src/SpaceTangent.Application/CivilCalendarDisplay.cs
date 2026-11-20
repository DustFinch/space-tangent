// SPDX-License-Identifier: GPL-2.0-or-later
using SpaceTangent.Contracts;
using SpaceTangent.Core.Observing;
using SpaceTangent.Core.Time;

namespace SpaceTangent.Application;

public static class CivilCalendarDisplay
{
    private static readonly string[] HebrewMonths = ["", "Nisan", "Iyar", "Sivan", "Tammuz", "Av", "Elul", "Tishri", "Heshvan", "Kislev", "Tevet", "Shevat", "Adar", "Adar II"];
    private static readonly string[] IslamicMonths = ["", "Muharram", "Safar", "Rabi I", "Rabi II", "Jumada I", "Jumada II", "Rajab", "Shaban", "Ramadan", "Shawwal", "Dhu al-Qadah", "Dhu al-Hijjah"];
    private static readonly string[] Tzolkin = ["Imix", "Ik", "Akbal", "Kan", "Chicchan", "Cimi", "Manik", "Lamat", "Muluc", "Oc", "Chuen", "Eb", "Ben", "Ix", "Men", "Cib", "Caban", "Etznab", "Cauac", "Ahau"];
    private static readonly string[] Haab = ["Pop", "Uo", "Zip", "Zotz", "Tzec", "Xul", "Yaxkin", "Mol", "Chen", "Yax", "Zac", "Ceh", "Mac", "Kankin", "Muan", "Pax", "Kayab", "Cumku", "Uayeb"];

    public static HistoricalDate? CivilDay(JulianInstant epoch, DeltaTModel model)
    {
        try { return HistoricalDate.FromGregorian(TimeConversion.ToUtc(epoch).Date); }
        catch (ArgumentOutOfRangeException) { return EarthCivilTime.ModeledHistoricalUt1(epoch, model)?.Date; }
    }

    public static string Format(HistoricalDate date)
    {
        long day = date.DayNumber;
        var julian = CalendarSystems.Julian(day);
        var iso = CalendarSystems.Iso(day);
        var hebrew = CalendarSystems.Hebrew(day);
        var islamic = CalendarSystems.Islamic(day);
        var maya = CalendarSystems.Maya(day);
        string month = hebrew is { Month: 12 } h && (7 * h.Year + 1) % 19 < 7 ? "Adar I" :
            hebrew is { } known ? HebrewMonths[known.Month] : "";
        return FormattableString.Invariant($"Civil day: {date}\nJulian: {julian.Year:D4}-{julian.Month:D2}-{julian.Day:D2}\n") +
            (iso is { } week ? FormattableString.Invariant($"ISO: {week.Year}-W{week.Week:D2}; day {week.DayOfYear}\n") : "ISO: unavailable\n") +
            (hebrew is { } hd ? FormattableString.Invariant($"Hebrew: {hd.Day} {month} {hd.Year}\n") : "Hebrew: before year 1\n") +
            FormattableString.Invariant($"Islamic (civil): {islamic.Day} {IslamicMonths[islamic.Month]} {islamic.Year}\n") +
            $"Maya: {maya.LongCount}\nTzolkin: {maya.TzolkinNumber} {Tzolkin[maya.TzolkinName]}\nHaab: {maya.HaabDay} {Haab[maya.HaabMonth]}";
    }
}
