# Microphone permission regression check

Validated on 2026-10-28 on a vivo V2529 running Android 16 with the debug APK.

The recorder previously checked microphone permission without requesting it.
It now requests permission on the UI thread and only permits capture after
both MAUI and Android report access granted.

## Device procedure

Use a development device with no active recording. Install the updated APK
without clearing app data. To reproduce first-use permission state, revoke
`android.permission.RECORD_AUDIO` for `io.github.sebastiancoss.kodamemo` and
clear its `user-set` and `user-fixed` permission flags with ADB.

1. Open KodaMemo and tap **Grabar reunión**. Wait for Android's microphone dialog.
2. Choose **No permitir**. Confirm the denial message and that no WAV was created.
3. Tap **Grabar reunión** again. Wait for the permission dialog and choose
   **Mientras la app está en uso**.
4. Record briefly, then tap **Detener y guardar**. Wait for the saved confirmation.
5. Confirm a new nonempty WAV exists and previous recordings remain intact.

All steps passed on the device. The accepted request produced a three-second
recording. Android build completed with zero warnings/errors; all seven existing
MSTest tests passed. These unit tests do not exercise the Android permission dialog.

Permanent denial, one-time grants and other Android versions remain outside this
check. Notification permissions and Whisper model provisioning are unchanged.
