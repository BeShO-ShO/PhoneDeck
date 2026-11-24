# mpv as a backup viewer

PhoneDeck's built-in viewer is the main way to show a capture device. If it doesn't work on
your PC, choose **mpv** in Settings → Phone's screen. PhoneDeck then finds `mpv.exe` on PATH or
in Program Files (or use the location you enter there), opens it full screen when capture
starts, and follows its window for touch.

Use **mpv 0.42 or newer**; 0.40 had a noticeable picture delay.

## Starting mpv yourself

This matches what PhoneDeck runs (replace `USB3 Video` with your device's name):

```
mpv av://dshow:video="USB3 Video" --cache=no --osc=no --fs --cursor-autohide=always --no-window-dragging --swapchain-depth=1 --video-latency-hacks=yes --d3d11-sync-interval=0 --input-ipc-server=\\.\pipe\phonedeck-mpv --input-conf="%LOCALAPPDATA%\PhoneDeck\mpv-input.conf"
```

- For sound, add `:audio="<audio device>"` after the video name, and
  `--demuxer-lavf-o=audio_buffer_size=50`.
- In a shortcut, write the `%LOCALAPPDATA%` part out in full, and make sure nothing (not even a
  line break) follows the last option.
- The input file stops the mouse wheel and double clicks changing mpv's volume or full screen.
  PhoneDeck writes it the first time it opens mpv itself.
- `--profile=low-latency`, `--untimed` and `--profile=fast` were all slower in testing.
