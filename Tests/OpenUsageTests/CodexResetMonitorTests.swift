import AppKit
import SwiftUI
import XCTest
@testable import OpenUsage

@MainActor
final class CodexResetMonitorTests: XCTestCase {
    @MainActor
    private final class Feed {
        var date = OpenUsageISO8601.date(from: "2026-10-11T02:00:00Z")!
        var posts: [(String, String, String, String)] = []
        var deliverySucceeds = true
        var metricEnabled = false
        let defaults: UserDefaults
        let settings: NotificationSettingsStore
        let http: RoutingHTTPClient

        init(defaults: UserDefaults) {
            self.defaults = defaults
            self.settings = NotificationSettingsStore(defaults: defaults)
            let responseSource = ResponseSource()
            self.http = RoutingHTTPClient { _ in
                await responseSource.read()
            }
            self.responseSource = responseSource
        }

        private let responseSource: ResponseSource

        func setBody(_ body: Data, code: Int = 200, headers: [String: String] = ["etag": "v1"]) async {
            await responseSource.set(HTTPResponse(statusCode: code, headers: headers, body: body))
        }

        func monitor() -> CodexResetMonitor {
            CodexResetMonitor(
                settings: settings, http: http, defaults: defaults,
                isMetricEnabled: { self.metricEnabled }, now: { self.date },
                post: { id, title, subtitle, body in
                    self.posts.append((id, title, subtitle, body))
                    return self.deliverySucceeds
                }
            )
        }
    }

    private actor ResponseSource {
        private var response = HTTPResponse(statusCode: 200, headers: ["etag": "v1"], body: CodexResetMonitorTests.body())
        func read() -> HTTPResponse { response }
        func set(_ response: HTTPResponse) { self.response = response }
    }

    private func feed() -> Feed {
        let name = "OpenUsageTests.reset-monitor.\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: name)!
        defaults.removePersistentDomain(forName: name)
        return Feed(defaults: defaults)
    }

    private nonisolated static func body(
        type: String = "regular", deadline: String? = "2026-10-11T07:00:00.000Z",
        scheduled: Bool = true, latest: String = "old", latestType: String = "banked"
    ) -> Data {
        let deadlineJSON = deadline.map { "\"\($0)\"" } ?? "null"
        let scheduledJSON = scheduled ? """
        {"id":"incoming","reset_type":"\(type)","announced_at":"2026-10-10T22:16:20Z","scheduled_for":\(deadlineJSON)}
        """ : "null"
        return Data("""
        {"data":{"latest_reset":{"id":"\(latest)","reset_type":"\(latestType)","announced_at":"2026-10-08T03:44:55.000Z"},
        "scheduled_reset":\(scheduledJSON),"active_watch":{"level":"strong","reset_chance_percent":95}}}
        """.utf8)
    }

    private var descriptor: WidgetDescriptor {
        .badge(id: "codex.nextReset", provider: CodexProvider.makeProvider(), title: "Next Reset")
    }

    func testBothIncomingResetTypesAlertWithoutSendingCredentialsOrHistoricalAlerts() async {
        for type in ["regular", "banked"] {
            let feed = feed()
            feed.settings.codexResetAnnouncements = true
            await feed.setBody(Self.body(type: type))
            await feed.monitor().refresh()

            XCTAssertEqual(feed.posts.count, 1)
            XCTAssertEqual(feed.posts.first?.1, "Codex Reset Incoming")
            XCTAssertEqual(feed.posts.first?.2, type == "banked" ? "Banked Reset" : "Regular Reset")
            XCTAssertEqual(feed.http.requests.first?.url, CodexResetAnnouncementsAPI.statusURL)
            XCTAssertEqual(feed.http.requests.first?.method, "GET")
            XCTAssertEqual(feed.http.requests.first?.headers, ["Accept": "application/json"])
        }
    }

    func testAlertsDeduplicateAcrossRestartAndCompletionIsSeparate() async {
        let feed = feed()
        feed.settings.codexResetAnnouncements = true
        await feed.monitor().refresh()
        let restarted = feed.monitor()
        await restarted.refresh()
        XCTAssertEqual(feed.posts.count, 1)

        feed.date += CodexResetMonitor.interval
        await feed.setBody(Self.body(scheduled: false, latest: "incoming", latestType: "regular"))
        await restarted.refresh()
        XCTAssertEqual(feed.posts.count, 2)
        XCTAssertEqual(feed.posts.last?.1, "Codex Reset Reported")
        XCTAssertFalse(restarted.widgetData(for: descriptor).showsInMenuBar)
        await feed.monitor().refresh()
        XCTAssertEqual(feed.posts.count, 2)
    }

    func testFailedDeliveryRetriesFromCachedStatusAndEnablingAlertsChecksCurrentSchedule() async {
        let feed = feed()
        feed.metricEnabled = true
        let monitor = feed.monitor()
        await monitor.refresh()
        XCTAssertTrue(feed.posts.isEmpty)
        feed.settings.codexResetAnnouncements = true
        feed.deliverySucceeds = false
        await monitor.refresh()
        feed.deliverySucceeds = true
        await monitor.refresh()
        await monitor.refresh()
        XCTAssertEqual(feed.posts.count, 2)
        XCTAssertEqual(feed.http.requests.count, 1)
    }

    func testConditionalRequestsAndBankedCompletion() async {
        let feed = feed()
        feed.settings.codexResetAnnouncements = true
        let monitor = feed.monitor()
        await monitor.refresh()
        feed.date += CodexResetMonitor.interval
        await feed.setBody(Data(), code: 304)
        await monitor.refresh()
        XCTAssertEqual(feed.http.requests.last?.headers["If-None-Match"], "v1")
        XCTAssertEqual(feed.posts.count, 1)
        feed.date += CodexResetMonitor.interval
        await feed.setBody(Self.body(scheduled: false, latest: "new-banked"))
        await monitor.refresh()
        XCTAssertEqual(feed.posts.last?.1, "Codex Banked Reset Available")
    }

    func testPassedDeadlineNeverClaimsCompletionAndUnknownTimeIsExplicit() async {
        let feed = feed()
        feed.metricEnabled = true
        await feed.setBody(Self.body(deadline: "2026-10-11T01:00:00Z"))
        let monitor = feed.monitor()
        await monitor.refresh()
        XCTAssertEqual(monitor.widgetData(for: descriptor).menuBarValue, "Awaiting Confirmation")
        feed.date += CodexResetMonitor.interval
        await feed.setBody(Self.body(type: "banked", deadline: nil))
        await monitor.refresh()
        XCTAssertEqual(monitor.widgetData(for: descriptor).unboundedDetail, "Time Not Announced")
        XCTAssertEqual(monitor.widgetData(for: descriptor).unboundedSubtitle, "Banked Reset")
    }

    func testWatchAloneDoesNotAlertOrShowExpectedReset() async {
        let feed = feed()
        feed.settings.codexResetAnnouncements = true
        await feed.setBody(Self.body(scheduled: false))
        let monitor = feed.monitor()
        await monitor.refresh()
        XCTAssertTrue(feed.posts.isEmpty)
        XCTAssertEqual(monitor.widgetData(for: descriptor).unboundedDetail, "Not Announced")
        XCTAssertFalse(monitor.widgetData(for: descriptor).showsInMenuBar)
    }

    func testRateLimitAndMalformedDataSurfaceErrorsWithoutStaleMenuBarTime() async {
        let feed = feed()
        feed.metricEnabled = true
        let monitor = feed.monitor()
        await monitor.refresh()
        feed.date += CodexResetMonitor.interval
        await feed.setBody(Data(), code: 429, headers: ["retry-after": "600"])
        await monitor.refresh()
        XCTAssertNotNil(monitor.errorMessage)
        XCTAssertFalse(monitor.widgetData(for: descriptor).showsInMenuBar)
        feed.date += CodexResetMonitor.interval
        await monitor.refresh()
        XCTAssertEqual(feed.http.requests.count, 2)
        feed.date += CodexResetMonitor.interval
        await feed.setBody(Self.body(deadline: "broken"))
        await monitor.refresh()
        XCTAssertNotNil(monitor.errorMessage)
        XCTAssertEqual(monitor.widgetData(for: descriptor).unboundedDetail, "Unavailable")
        feed.date += CodexResetMonitor.interval
        await feed.setBody(Self.body())
        await monitor.refresh()
        XCTAssertNil(monitor.errorMessage)
        XCTAssertTrue(monitor.widgetData(for: descriptor).showsInMenuBar)
    }

    func testDisabledMonitorDoesNotFetch() async {
        let feed = feed()
        await feed.monitor().refresh()
        XCTAssertTrue(feed.http.requests.isEmpty)
    }

    func testMenuBarGetsExpectedTimeWithoutAnAuthenticatedSnapshotInBothStyles() async throws {
        let feed = feed()
        feed.metricEnabled = true
        let monitor = feed.monitor()
        let provider = CodexProvider.makeProvider()
        let registry = WidgetRegistry(providers: [provider], descriptors: [descriptor])
        let store = WidgetDataStore(registry: registry, providers: [], defaults: feed.defaults, codexResetMonitor: monitor)
        await monitor.refresh()
        let session = WidgetDescriptor.percent(id: "codex.session", provider: provider, title: "Session")
        let weekly = WidgetDescriptor.percent(id: "codex.weekly", provider: provider, title: "Weekly")
        let content = MenuBarContentBuilder.build(
            groups: [ProviderMetrics(provider: provider, metrics: [session, weekly, descriptor])],
            data: {
                if CodexResetMonitor.isNextReset($0) { return store.data(for: $0) }
                return WidgetData(title: $0.title, icon: provider.icon, kind: .percent,
                                  used: $0.id == session.id ? 41 : 12, limit: 100)
            }
        )
        XCTAssertEqual(content.groups.first?.metrics.last?.label, "Next Reset")
        XCTAssertTrue(content.groups.first?.metrics.last?.value.hasPrefix("By ") == true)
        let textImage = try XCTUnwrap(MenuBarStripRenderer.textImage(for: content))
        let barsImage = try XCTUnwrap(MenuBarStripRenderer.barsImage(for: content))
        XCTAssertLessThanOrEqual(textImage.size.height, 24, "A third pin must not make the menu-bar strip taller")
        XCTAssertFalse(store.data(for: descriptor).isBounded)

        // Optional previews use the production views and renderers for visual review.
        if let directory = ProcessInfo.processInfo.environment["OPENUSAGE_RESET_PREVIEW_DIR"] {
            let url = URL(fileURLWithPath: directory, isDirectory: true)
            try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
            for (name, image) in [("menu-bar-text", textImage), ("menu-bar-bars", barsImage)] {
                let preview = NSImage(size: NSSize(width: image.size.width + 16, height: image.size.height + 16))
                preview.lockFocus()
                NSColor.white.setFill()
                NSRect(origin: .zero, size: preview.size).fill()
                image.draw(at: NSPoint(x: 8, y: 8), from: .zero, operation: .sourceOver, fraction: 1)
                preview.unlockFocus()
                let tiff = try XCTUnwrap(preview.tiffRepresentation)
                let bitmap = try XCTUnwrap(NSBitmapImageRep(data: tiff))
                let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
                try png.write(to: url.appendingPathComponent(name + ".png"))
            }
            let view = NSHostingView(rootView: VStack(alignment: .leading, spacing: 16) {
                WidgetRowView(data: store.data(for: descriptor)).cardSurface()
                CodexResetAlertsSection(settings: feed.settings, monitor: monitor).cardSurface()
            }
                .padding(16)
                .frame(width: 370)
                .background(Color(nsColor: .windowBackgroundColor)))
            view.setFrameSize(view.fittingSize)
            view.layoutSubtreeIfNeeded()
            let bitmap = try XCTUnwrap(view.bitmapImageRepForCachingDisplay(in: view.bounds))
            view.cacheDisplay(in: view.bounds, to: bitmap)
            let png = try XCTUnwrap(bitmap.representation(using: .png, properties: [:]))
            try png.write(to: url.appendingPathComponent("reset-controls.png"))
        }
    }
}
