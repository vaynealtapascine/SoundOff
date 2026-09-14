namespace SoundOff.Core;

public static class SyntheticFixture
{
    // Fixed IDs make the authored provider deterministic; each project still gets its own identity.
    public static Transcript Create(Guid projectId, long baseRevision)
    {
        var a = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var b = Guid.Parse("10000000-0000-0000-0000-000000000002");
        return new Transcript(projectId, "Synthetic demo — editing practice", baseRevision, Provenance.Synthetic,
            [new(a, "Demo speaker A"), new(b, "Demo speaker B")],
            [new(Guid.Parse("20000000-0000-0000-0000-000000000001"), a,
                "This is an authored synthetic example, not a transcript from audio. Rename a speaker or edit this paragraph, then save.", null),
             new(Guid.Parse("20000000-0000-0000-0000-000000000002"), b,
                "Kumusta! Halimbawang teksto lang ito. Unicode stays intact: José, piña, café, 中文, 👩🏽‍💻.", null),
             new(Guid.Parse("20000000-0000-0000-0000-000000000003"), a,
                "No words have measured timestamps. Undo restores saved edits; TXT and clipboard export preserve this synthetic provenance.", null)]);
    }
}
