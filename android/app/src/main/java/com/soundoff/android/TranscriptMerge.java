package com.soundoff.android;

final class TranscriptMerge {
    static String preserveUnseen(String edited, String recognized, int seenCharacters) {
        if (seenCharacters < 0 || seenCharacters > recognized.length()) throw new IllegalArgumentException("Invalid transcript snapshot");
        return edited + recognized.substring(seenCharacters);
    }
}
