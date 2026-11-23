namespace Kyc.Domain;

public enum CheckOutcome
{
    Passed,
    // Tampered document, spoof or different person.
    Failed,
    // Bad image (blur, glare). Customer can recapture.
    Unreadable,
    // Low-confidence result. Goes to a human.
    Inconclusive,
}
