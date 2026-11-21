import QtQuick
import Quickshell
import Quickshell.Wayland
import Quickshell.Services.Notifications
import "lib/notifications.mjs" as N

// Notifications from apps (the freedesktop notification service: the shell
// is the server, so email, downloads, chat and calendar reminders appear here):
//  - a card in the top right for each new one, gone after a few seconds
//    (critical ones stay until dismissed); its buttons are the app's actions;
//  - the notification centre (the bell in the top bar): what came in and was
//    not dismissed, newest first, with Clear all;
//  - Do not disturb (Settings, Desktop, or the centre): cards stop, except
//    critical ones; everything still waits in the centre.
// Text is shown as plain text: an app cannot put links or markup in front of the person.
Scope {
    id: notifications
    property var theme
    property var desktop                 // DesktopSettings: doNotDisturb
    property var translations: null
    function tr(text) { return translations ? translations.t(text) : text }
    property bool centreOpen: false
    readonly property bool doNotDisturb: !!desktop && desktop.doNotDisturb
    // Newest first, from the server's tracked notifications.
    readonly property var all: server.trackedNotifications.values.slice().reverse()
    readonly property int count: all.length
    property var popups: []              // shown as cards now

    function clearAll() { for (const n of server.trackedNotifications.values.slice()) n.dismiss() }
    function hidePopup(n) { popups = popups.filter(p => p !== n) }
    // A click on a notification: its default action if it has one, then it goes.
    function activate(n) {
        const action = N.defaultAction(n.actions);
        if (action) action.invoke();
        n.dismiss();
        hidePopup(n);
    }

    NotificationServer {
        id: server
        keepOnReload: true
        persistenceSupported: true
        bodySupported: true
        bodyMarkupSupported: false
        bodyHyperlinksSupported: false
        actionsSupported: true
        imageSupported: true
        onNotification: n => {
            n.tracked = true;            // kept for the centre until dismissed
            if (N.showCard(n.urgency === NotificationUrgency.Critical, notifications.doNotDisturb, notifications.centreOpen))
                notifications.popups = [n].concat(notifications.popups).slice(0, N.MAX_CARDS);
        }
    }

    // The cards, top right, under the top bar.
    PanelWindow {
        id: cards
        visible: notifications.popups.length > 0
        anchors { top: true; right: true }
        margins { top: 56; right: 14 }
        implicitWidth: 380
        implicitHeight: stack.implicitHeight
        color: "transparent"
        exclusiveZone: 0
        WlrLayershell.namespace: "nexus-notifications"
        WlrLayershell.layer: WlrLayer.Overlay
        Column {
            id: stack
            width: parent.width
            spacing: 10
            Repeater {
                model: notifications.popups
                delegate: NotificationCard {
                    required property var modelData
                    width: stack.width
                    theme: notifications.theme
                    notification: modelData
                    onActivated: notifications.activate(modelData)
                    onClosed: { modelData.dismiss(); notifications.hidePopup(modelData) }
                    Timer {
                        // Critical ones stay; the rest go after the app's timeout, or 6 seconds.
                        running: modelData.urgency !== NotificationUrgency.Critical
                        interval: N.cardTimeout(modelData.expireTimeout)
                        onTriggered: notifications.hidePopup(modelData)
                    }
                }
            }
        }
    }

    // The centre: what came in, newest first.
    PanelWindow {
        visible: notifications.centreOpen
        anchors { top: true; right: true }
        margins { top: 56; right: 14 }
        implicitWidth: 400
        implicitHeight: Math.min(560, centre.implicitHeight + 32)
        color: "transparent"
        exclusiveZone: 0
        WlrLayershell.namespace: "nexus-notification-centre"
        WlrLayershell.layer: WlrLayer.Top
        WlrLayershell.keyboardFocus: WlrKeyboardFocus.OnDemand
        Rectangle {
            anchors.fill: parent
            radius: 18
            color: notifications.theme.glass
            border.color: notifications.theme.glassBorder
            focus: true
            Keys.onEscapePressed: notifications.centreOpen = false
            Flickable {
                anchors { fill: parent; margins: 16 }
                contentHeight: centre.implicitHeight
                clip: true
                boundsBehavior: Flickable.StopAtBounds
                Column {
                    id: centre
                    width: parent.width
                    spacing: 10
                    Item {
                        width: parent.width; height: 28
                        Text { anchors.verticalCenter: parent.verticalCenter; text: notifications.tr("Notifications")
                               color: notifications.theme.ink; font.pixelSize: 16; font.weight: 600 }
                        Row {
                            anchors { right: parent.right; verticalCenter: parent.verticalCenter }
                            spacing: 14
                            Text {
                                text: notifications.tr("Do not disturb") + ": " + notifications.tr(notifications.doNotDisturb ? "On" : "Off")
                                color: notifications.doNotDisturb ? notifications.theme.focus : notifications.theme.inkSecondary
                                font.pixelSize: 12
                                HoverHandler { cursorShape: Qt.PointingHandCursor }
                                TapHandler { onTapped: notifications.desktop.set("doNotDisturb", !notifications.doNotDisturb) }
                                Accessible.role: Accessible.CheckBox
                                Accessible.checked: notifications.doNotDisturb
                                Accessible.name: notifications.tr("Do not disturb")
                            }
                            Text {
                                visible: notifications.count > 0
                                text: notifications.tr("Clear all"); color: notifications.theme.inkSecondary; font.pixelSize: 12
                                HoverHandler { cursorShape: Qt.PointingHandCursor }
                                TapHandler { onTapped: notifications.clearAll() }
                                Accessible.role: Accessible.Button
                                Accessible.name: "Clear all notifications"
                            }
                        }
                    }
                    Text {
                        visible: notifications.count === 0
                        width: parent.width; wrapMode: Text.Wrap
                        text: notifications.tr("Nothing new. Notifications from apps appear here until you clear them.")
                        color: notifications.theme.inkSecondary; font.pixelSize: 13
                    }
                    Repeater {
                        model: notifications.all
                        delegate: NotificationCard {
                            required property var modelData
                            width: centre.width
                            theme: notifications.theme
                            notification: modelData
                            onActivated: { notifications.activate(modelData); notifications.centreOpen = false }
                            onClosed: modelData.dismiss()
                        }
                    }
                }
            }
        }
    }
}
