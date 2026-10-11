import Foundation

/// The public tracker's explicit announcements. Forecast watches are deliberately not decoded.
struct CodexResetStatus: Decodable, Sendable {
    let latestReset: CodexResetAnnouncement?
    let scheduledReset: CodexScheduledReset?
}

enum CodexResetType: String, Decodable, Sendable {
    case regular, banked

    var label: String { self == .banked ? "Banked Reset" : "Regular Reset" }
}

struct CodexResetAnnouncement: Decodable, Sendable {
    let id: String
    let resetType: CodexResetType
    let announcedAt: Date
}

struct CodexScheduledReset: Decodable, Sendable {
    let id: String
    let resetType: CodexResetType
    let announcedAt: Date
    let scheduledFor: Date?

    func timing(now: Date) -> String {
        guard let scheduledFor else { return "Time Not Announced" }
        guard scheduledFor > now else { return "Awaiting Confirmation" }
        let time = TimeFormatSetting.current.shortTime(scheduledFor)
        if Calendar.current.isDate(scheduledFor, inSameDayAs: now) { return "By \(time)" }
        return "By \(Formatters.monthDayLabel(scheduledFor)), \(time)"
    }
}

enum CodexResetAnnouncementsAPI {
    static let siteURL = URL(string: "https://codex-resets.com/")!
    static let statusURL = URL(string: "https://codex-resets.com/api/v1/status")!

    private struct Envelope: Decodable {
        let data: CodexResetStatus
    }

    static func decode(_ body: Data) throws -> CodexResetStatus {
        let decoder = JSONDecoder()
        decoder.keyDecodingStrategy = .convertFromSnakeCase
        decoder.dateDecodingStrategy = .custom { decoder in
            let container = try decoder.singleValueContainer()
            let value = try container.decode(String.self)
            guard let date = OpenUsageISO8601.date(from: value) else {
                throw DecodingError.dataCorruptedError(in: container, debugDescription: "Invalid reset announcement date")
            }
            return date
        }
        return try decoder.decode(Envelope.self, from: body).data
    }
}
