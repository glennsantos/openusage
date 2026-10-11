import XCTest
@testable import OpenUsage

/// Covers the menu-bar pin model on `LayoutStore`: the three-per-provider cap, denial
/// reasons/notices, order derivation from the Customize order,
/// disabled-provider handling, and persistence across relaunch.
@MainActor
final class MenuBarPinTests: XCTestCase {
    func testNoPinsByDefault() {
        let store = makeStore("default")
        XCTAssertTrue(store.pinnedMetricIDs.isEmpty)
        XCTAssertTrue(store.pinnedGroups.isEmpty)
        XCTAssertEqual(store.menuBarStyle, .text)
    }

    func testPinUnpinPersistsAcrossReload() {
        let defaults = makeDefaults("persist")
        let store = LayoutStore(registry: makeRegistry(), defaults: defaults, storageKey: "layout")

        store.setPinned(true, for: "a.m1")
        XCTAssertTrue(store.isPinned("a.m1"))

        let reloaded = LayoutStore(registry: makeRegistry(), defaults: defaults, storageKey: "layout")
        XCTAssertTrue(reloaded.isPinned("a.m1"))

        reloaded.setPinned(false, for: "a.m1")
        let reloadedAgain = LayoutStore(registry: makeRegistry(), defaults: defaults, storageKey: "layout")
        XCTAssertFalse(reloadedAgain.isPinned("a.m1"))
    }

    func testPerProviderCapAllowsThirdAndBlocksFourthUntilSlotIsFreed() {
        let store = makeStore("perProvider")
        store.setPinned(true, for: "a.m1")
        store.setPinned(true, for: "a.m2")
        XCTAssertTrue(store.canPin("a.m3"))
        store.setPinned(true, for: "a.m3")
        XCTAssertTrue(store.isPinned("a.m3"))
        XCTAssertFalse(store.canPin("a.m4"))
        store.setPinned(true, for: "a.m4")
        XCTAssertFalse(store.isPinned("a.m4"))
        XCTAssertEqual(store.pinnedCount(forProvider: "a"), 3)

        // An already-pinned id stays pinnable so its toggle can still unpin it.
        XCTAssertTrue(store.canPin("a.m1"))

        store.setPinned(false, for: "a.m1")
        XCTAssertTrue(store.canPin("a.m4"))
    }

    func testEachProviderCanPinUpToThree() {
        let store = makeStore("manyProviders")
        for provider in ["a", "b"] {
            store.setPinned(true, for: "\(provider).m1")
            store.setPinned(true, for: "\(provider).m2")
            store.setPinned(true, for: "\(provider).m3")
            XCTAssertFalse(store.canPin("\(provider).m4"))
        }
        XCTAssertEqual(store.pinnedMetricIDs.count, 6)
    }

    func testPinDenialReasonsAndFooterNotice() {
        let store = makeStore("denial")
        XCTAssertNil(store.pinDenialReason("a.m1"))

        store.setPinned(true, for: "a.m1")
        store.setPinned(true, for: "a.m2")
        store.setPinned(true, for: "a.m3")
        XCTAssertEqual(store.pinDenialReason("a.m4"), "Up to 3 stars per provider")
        XCTAssertNil(store.pinDenialReason("b.m1"))

        // A denied click surfaces the reason as the transient footer notice and bumps the shake
        // trigger every time, so repeat clicks re-shake even while the text is unchanged.
        XCTAssertNil(store.pinLimitNotice)
        XCTAssertEqual(store.pinNoticeShakeTrigger, 0)
        store.notePinDenied("a.m4")
        XCTAssertEqual(store.pinLimitNotice, "Up to 3 stars per provider")
        XCTAssertEqual(store.pinNoticeShakeTrigger, 1)
        store.notePinDenied("a.m4")
        XCTAssertEqual(store.pinNoticeShakeTrigger, 2)
    }

    func testPinnedGroupsFollowCustomizeOrder() {
        let store = makeStore("order")
        // Pin out of order; expect provider order (a before b) and metric order (m1 before m2).
        store.setPinned(true, for: "b.m2")
        store.setPinned(true, for: "a.m2")
        store.setPinned(true, for: "a.m1")

        XCTAssertEqual(store.pinnedGroups.flatMap { $0.metrics.map(\.id) }, ["a.m1", "a.m2", "b.m2"])
        XCTAssertEqual(store.pinnedGroups.map(\.provider.id), ["a", "b"])
    }

    func testDisabledProviderPinsExcludedFromGroupsButKept() {
        let store = LayoutStore(
            registry: makeRegistry(),
            defaults: makeDefaults("disabled"),
            storageKey: "layout",
            isProviderEnabled: { $0 != "a" }
        )
        store.setPinned(true, for: "a.m1")
        store.setPinned(true, for: "b.m1")

        XCTAssertEqual(store.pinnedGroups.map(\.provider.id), ["b"])
        XCTAssertTrue(store.isPinned("a.m1"))  // membership preserved while hidden
    }

    func testResetToDefaultClearsPins() {
        let store = makeStore("reset")
        store.setPinned(true, for: "a.m1")
        store.resetToDefault()
        XCTAssertTrue(store.pinnedMetricIDs.isEmpty)
    }

    func testMenuBarStylePersists() {
        let defaults = makeDefaults("style")
        let store = LayoutStore(registry: makeRegistry(), defaults: defaults, storageKey: "layout")
        store.menuBarStyle = .bars

        let reloaded = LayoutStore(registry: makeRegistry(), defaults: defaults, storageKey: "layout")
        XCTAssertEqual(reloaded.menuBarStyle, .bars)
    }

    func testInvalidPinnedIDsDroppedOnLoad() {
        let defaults = makeDefaults("invalid")
        defaults.set(["a.m1", "ghost.metric"], forKey: "layout.menuBarPins")
        let store = LayoutStore(registry: makeRegistry(), defaults: defaults, storageKey: "layout")

        XCTAssertTrue(store.isPinned("a.m1"))
        XCTAssertFalse(store.isPinned("ghost.metric"))
    }

    func testNextResetStaysUnpinnedOnUpgradeAndCanUseThirdSlotManually() {
        let provider = CodexProvider()
        let registry = WidgetRegistry.from([provider])
        let defaults = makeDefaults("next-reset-upgrade")
        let old = LayoutStore(
            registry: registry, defaults: defaults, storageKey: "layout",
            defaultMetricIDs: DefaultLayout.metricIDs.filter { $0 != "codex.nextReset" },
            defaultPinnedMetricIDs: ["codex.session", "codex.weekly"]
        )
        old.persist()
        old.persistPins()

        let upgraded = LayoutStore(registry: registry, defaults: defaults, storageKey: "layout")
        XCTAssertTrue(upgraded.isMetricEnabled("codex.nextReset"))
        XCTAssertFalse(upgraded.expandedMetricIDs.contains("codex.nextReset"))
        XCTAssertFalse(upgraded.isPinned("codex.nextReset"))
        upgraded.setPinned(true, for: "codex.nextReset")
        XCTAssertTrue(upgraded.isPinned("codex.nextReset"))
        XCTAssertEqual(upgraded.pinnedCount(forProvider: "codex"), 3)
        upgraded.setPinned(false, for: "codex.nextReset")
        XCTAssertFalse(LayoutStore(registry: registry, defaults: defaults, storageKey: "layout").isPinned("codex.nextReset"))

        let emptyDefaults = makeDefaults("next-reset-empty-upgrade")
        let empty = LayoutStore(
            registry: registry, defaults: emptyDefaults, storageKey: "layout",
            defaultMetricIDs: DefaultLayout.metricIDs.filter { $0 != "codex.nextReset" },
            defaultPinnedMetricIDs: []
        )
        empty.persist()
        empty.persistPins()
        XCTAssertTrue(LayoutStore(registry: registry, defaults: emptyDefaults, storageKey: "layout").pinnedMetricIDs.isEmpty)
    }

    // MARK: - Fixtures

    private func makeStore(_ name: String) -> LayoutStore {
        LayoutStore(registry: makeRegistry(), defaults: makeDefaults(name), storageKey: "layout")
    }

    /// Two providers, each with three percent metrics, are sufficient to exercise provider-local caps.
    private func makeRegistry() -> WidgetRegistry {
        let providers = ["a", "b"].map { id in
            Provider(id: id, displayName: id.uppercased(), icon: .providerMark("cursor"))
        }
        let descriptors = providers.flatMap { provider in
            (1...4).map { n in metric(provider, id: "\(provider.id).m\(n)", label: "M\(n)") }
        }
        return WidgetRegistry(providers: providers, descriptors: descriptors)
    }

    private func metric(_ provider: Provider, id: String, label: String) -> WidgetDescriptor {
        WidgetDescriptor(
            id: id,
            providerID: provider.id,
            metricLabel: label,
            sample: WidgetData(
                title: label,
                icon: provider.icon,
                kind: .percent,
                used: 10,
                limit: 100
            )
        )
    }

    private func makeDefaults(_ name: String) -> UserDefaults {
        let suiteName = "OpenUsageTests.MenuBarPin.\(name).\(UUID().uuidString)"
        let defaults = UserDefaults(suiteName: suiteName)!
        defaults.removePersistentDomain(forName: suiteName)
        return defaults
    }
}
