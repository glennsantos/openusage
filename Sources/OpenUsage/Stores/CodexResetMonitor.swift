import Foundation
import Observation

/// One public feed for all Codex accounts, independent of authenticated quota refreshes.
@MainActor
@Observable
final class CodexResetMonitor {
    static let interval: TimeInterval = 5 * 60
    private static let deliveredKey = "openusage.codexResets.delivered"
    private static let baselineKey = "openusage.codexResets.baseline"

    private(set) var status: CodexResetStatus?
    private(set) var errorMessage: String?
    private let http: any HTTPClient
    private let defaults: UserDefaults
    private let settings: NotificationSettingsStore
    private let isMetricEnabled: () -> Bool
    private let now: () -> Date
    private let post: (String, String, String, String) async -> Bool
    private var etag: String?
    private var nextFetchAt = Date.distantPast
    private var isRefreshing = false
    private var delivered: [String]

    init(
        settings: NotificationSettingsStore,
        http: any HTTPClient = URLSessionHTTPClient(),
        defaults: UserDefaults = .standard,
        isMetricEnabled: @escaping () -> Bool = { false },
        now: @escaping () -> Date = Date.init,
        post: ((String, String, String, String) async -> Bool)? = nil
    ) {
        self.settings = settings
        self.http = http
        self.defaults = defaults
        self.isMetricEnabled = isMetricEnabled
        self.now = now
        self.post = post ?? { id, title, subtitle, body in
            await AppNotifications.shared.post(idPrefix: id, title: title, subtitle: subtitle, body: body)
        }
        self.delivered = defaults.stringArray(forKey: Self.deliveredKey) ?? []
    }

    static func isNextReset(_ descriptor: WidgetDescriptor) -> Bool {
        ProviderAccountID.family(of: descriptor.providerID) == "codex"
            && descriptor.id == "\(descriptor.providerID).nextReset"
    }

    func refresh() async {
        guard settings.codexResetAnnouncements || isMetricEnabled(), !isRefreshing else { return }
        isRefreshing = true
        defer { isRefreshing = false }
        guard now() >= nextFetchAt else {
            if errorMessage == nil { await notify() }
            return
        }
        nextFetchAt = now().addingTimeInterval(Self.interval)
        var headers = ["Accept": "application/json"]
        if let etag { headers["If-None-Match"] = etag }
        do {
            let response = try await http.send(HTTPRequest(method: "GET", url: CodexResetAnnouncementsAPI.statusURL, headers: headers))
            guard !Task.isCancelled else { return }
            switch response.statusCode {
            case 200:
                status = try CodexResetAnnouncementsAPI.decode(response.body)
                etag = response.header("ETag")
            case 304 where status != nil:
                break
            case 429:
                if let value = response.header("Retry-After"), let delay = TimeInterval(value), delay.isFinite, delay > 0 {
                    nextFetchAt = now().addingTimeInterval(max(Self.interval, delay))
                }
                throw FeedError.http(response.statusCode)
            default:
                throw FeedError.http(response.statusCode)
            }
            errorMessage = nil
            await notify()
        } catch {
            guard !Task.isCancelled else { return }
            errorMessage = "Couldn't check Codex reset announcements. OpenUsage will retry automatically."
            AppLog.error(.notifications, "Codex reset tracker failed: \(error.localizedDescription)")
        }
    }

    func widgetData(for descriptor: WidgetDescriptor) -> WidgetData {
        var data = descriptor.sample
        data.sourceLink = WidgetData.SourceLink(label: "Codex Resets", url: CodexResetAnnouncementsAPI.siteURL)
        if errorMessage != nil {
            data.valueTextOverride = "Unavailable"
            data.showsInMenuBar = false
        } else if let status {
            if let scheduled = status.scheduledReset {
                data.valueTextOverride = scheduled.timing(now: now())
                data.subtitleOverride = scheduled.resetType.label
            } else {
                data.valueTextOverride = "Not Announced"
                data.showsInMenuBar = false
            }
        } else {
            data.hasData = false
        }
        return data
    }

    private func notify() async {
        guard let status else { return }
        // First contact establishes a historical baseline, even when only the metric is enabled.
        // An already-scheduled reset still alerts immediately when notifications are turned on.
        if defaults.string(forKey: Self.baselineKey) == nil {
            defaults.set(status.latestReset?.id ?? "none", forKey: Self.baselineKey)
        }
        guard settings.codexResetAnnouncements, !Task.isCancelled else { return }
        if let scheduled = status.scheduledReset {
            let deadline = scheduled.scheduledFor.map(OpenUsageISO8601.string) ?? "unknown"
            await deliver(
                key: "scheduled:\(scheduled.id):\(deadline)",
                title: "Codex Reset Incoming",
                subtitle: scheduled.resetType.label,
                body: "\(scheduled.timing(now: now())). Announced reset tracked by codex-resets.com; timing may change."
            )
        }
        if let reset = status.latestReset, reset.id != defaults.string(forKey: Self.baselineKey) {
            await deliver(
                key: "completed:\(reset.id)",
                title: reset.resetType == .banked ? "Codex Banked Reset Available" : "Codex Reset Reported",
                subtitle: reset.resetType.label,
                body: reset.resetType == .banked
                    ? "codex-resets.com reports a new banked reset credit. Check your account for availability."
                    : "codex-resets.com reports a regular reset has landed. Check your account for updated limits."
            )
        }
    }

    private func deliver(key: String, title: String, subtitle: String, body: String) async {
        guard settings.codexResetAnnouncements, !delivered.contains(key), !Task.isCancelled else { return }
        guard await post("codex-reset-\(key)", title, subtitle, body) else { return }
        delivered.append(key)
        delivered = Array(delivered.suffix(200))
        defaults.set(delivered, forKey: Self.deliveredKey)
    }

    private enum FeedError: LocalizedError {
        case http(Int)
        var errorDescription: String? {
            if case .http(let code) = self { return "Reset tracker returned HTTP \(code)." }
            return nil
        }
    }
}
