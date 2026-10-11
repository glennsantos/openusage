import SwiftUI

struct CodexResetAlertsSection: View {
    @Bindable var settings: NotificationSettingsStore
    let monitor: CodexResetMonitor
    @AppStorage(DensitySetting.key) private var density = DensitySetting.regular

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Text("Codex Reset Announcements")
                Spacer(minLength: 8)
                Toggle("", isOn: $settings.codexResetAnnouncements)
                    .settingsSwitchStyle()
                    .accessibilityLabel("Codex Reset Announcements")
            }
            Text("Alerts for incoming banked and regular resets, and when they land.")
                .font(.caption)
                .foregroundStyle(.secondary)
            Link("Data from codex-resets.com", destination: CodexResetAnnouncementsAPI.siteURL)
                .font(.caption)
            if let error = monitor.errorMessage {
                Text(error)
                    .font(.caption)
                    .foregroundStyle(.orange)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, density.controlRowPadding)
        .onChange(of: settings.codexResetAnnouncements) { _, enabled in
            if enabled {
                AppNotifications.shared.requestAuthorization()
                Task { await monitor.refresh() }
            }
        }
    }
}
