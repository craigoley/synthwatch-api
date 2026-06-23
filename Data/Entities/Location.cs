namespace SynthWatch.Api.Data.Entities;

/// <summary>
/// The deployed-regions registry (runner migration #73 / 4-MLACT). Keyed on <see cref="Name"/>; a check
/// runs from the active locations it has a check_locations cursor for. Read-only here (the runner owns
/// the registry); the API reads it for the location selector and to validate assignment writes.
/// </summary>
public class Location
{
    public string Name { get; set; } = null!;
    public bool Enabled { get; set; }
}
