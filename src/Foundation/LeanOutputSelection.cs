namespace AutoFollower;

public sealed record LeanOutputSelection(bool Prebuilt = true, bool Obody = true);
public sealed record LeanOutputPlan(bool Prebuilt, bool Obody, bool ObodyPresent, string[] Variants);

public static class LeanOutputPolicy
{
    // Older requests preserve both applicable outputs, or the non-OBody as-is route.
    public static LeanOutputSelection Resolve(LeanOutputSelection? selected, bool obodyPresent, bool? expectedObodyPresent = null, bool bestEffort=false)
    {
        if (!bestEffort && expectedObodyPresent.HasValue && expectedObodyPresent.Value != obodyPresent)
            throw new InvalidDataException("OBody availability changed since input preparation. Refresh the selected environment and review the output choices.");
        selected ??= new(true, obodyPresent);
        if (!selected.Prebuilt && !selected.Obody)
            throw new InvalidDataException("Select at least one output: Prebuilt or OBody NG.");
        if (!bestEffort && selected.Obody && !obodyPresent)
            throw new InvalidDataException("OBody NG is unavailable in the selected MO2 environment. Select Prebuilt or refresh the selected environment.");
        return selected;
    }

    public static bool CapturedObody(LeanOutputSelection selected,bool obodyPresent,LeanCosave cosave)
    {
        if(!obodyPresent)return false;
        if(cosave.Preset!=null)return true;
        // No assignment is invented. Requested branches can preserve captured
        // geometry and disclose that preset baking/application is unavailable.
        return false;
    }
    public static LeanOutputPlan Plan(LeanOutputSelection selected, bool obodyPresent,bool capturedObody=true) =>
        new(selected.Prebuilt, selected.Obody, obodyPresent,
            obodyPresent&&capturedObody||selected.Obody ? (selected.Prebuilt ? (selected.Obody ? ["prebuilt", "obody"] : ["prebuilt"]) : ["obody"]) : ["as-is"]);
}
