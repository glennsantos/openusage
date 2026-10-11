import Foundation
import Observation

/// User preferences for quota pace and Codex announcement notifications. All default OFF;
/// the app requests notification authorization the
/// first time a trigger is turned on, so a fresh install stays quiet until the user opts in.
///
/// Persisted in `UserDefaults` (each key independently, with an unset key defaulting to `false`).
/// `@Observable` lets the Settings toggles and `WidgetDataStore` evaluation read live values.
@MainActor
@Observable
final class NotificationSettingsStore {
    private let defaults: UserDefaults

    private static let underTenKey = "openusage.notifications.underTenPercent"
    private static let healthyToCloseKey = "openusage.notifications.healthyToClose"
    private static let closeToRunningOutKey = "openusage.notifications.closeToRunningOut"
    private static let codexResetKey = "openusage.notifications.codexResetAnnouncements"

    var codexResetAnnouncements: Bool {
        didSet { defaults.set(codexResetAnnouncements, forKey: Self.codexResetKey) }
    }

    /// Alert the first time a metric drops under 10% remaining for the period.
    var underTenPercent: Bool {
        didSet { defaults.set(underTenPercent, forKey: Self.underTenKey) }
    }

    /// Alert when pace worsens from healthy (blue) to close-to-limit (yellow).
    var healthyToClose: Bool {
        didSet { defaults.set(healthyToClose, forKey: Self.healthyToCloseKey) }
    }

    /// Alert when pace worsens from close-to-limit (yellow) to running-out (red).
    var closeToRunningOut: Bool {
        didSet { defaults.set(closeToRunningOut, forKey: Self.closeToRunningOutKey) }
    }

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        self.underTenPercent = defaults.bool(forKey: Self.underTenKey, default: false)
        self.healthyToClose = defaults.bool(forKey: Self.healthyToCloseKey, default: false)
        self.closeToRunningOut = defaults.bool(forKey: Self.closeToRunningOutKey, default: false)
        self.codexResetAnnouncements = defaults.bool(forKey: Self.codexResetKey, default: false)
    }

    /// The per-milestone toggles as the pure logic consumes them.
    var toggles: PaceNotificationToggles {
        PaceNotificationToggles(
            underTenPercent: underTenPercent,
            healthyToClose: healthyToClose,
            closeToRunningOut: closeToRunningOut
        )
    }

    /// Shared gate for notification authorization and the Settings permission notice.
    var anyEnabled: Bool { underTenPercent || healthyToClose || closeToRunningOut || codexResetAnnouncements }

    /// Restores every alert to its default (off).
    func resetToDefaults() {
        underTenPercent = false
        healthyToClose = false
        closeToRunningOut = false
        codexResetAnnouncements = false
    }
}
