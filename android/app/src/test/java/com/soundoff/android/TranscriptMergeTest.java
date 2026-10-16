package com.soundoff.android;
import org.junit.Test;
import static org.junit.Assert.*;

public class TranscriptMergeTest {
    @Test public void keepsNewPassageAfterUserCorrectsEarlierWords() {
        assertEquals("Corrected Filipino\n\nNew passage", TranscriptMerge.preserveUnseen("Corrected Filipino", "Old passage\n\nNew passage", "Old passage".length()));
    }
    @Test public void keepsDeliberateDeletionWithoutRestoringSeenWords() {
        assertEquals("\n\nNew", TranscriptMerge.preserveUnseen("", "Old\n\nNew", 3));
    }
    @Test(expected = IllegalArgumentException.class) public void rejectsForeignSnapshot() {
        TranscriptMerge.preserveUnseen("Correction", "Raw", 20);
    }
}
