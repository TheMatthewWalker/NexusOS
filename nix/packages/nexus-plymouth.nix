# The NexusOS start-up screen (Plymouth): the Nexus core on deep space, with
# Plymouth's own spinner and password box (its two-step module, as in its
# "spinner" theme), so the prompt that unlocks the disk is part of it.
# The mark is nix/branding/nexusos-logo.svg, drawn to a picture at build time.
{ runCommand, librsvg, plymouth }:
runCommand "nexusos-plymouth-theme" { nativeBuildInputs = [ librsvg ]; } ''
  theme=$out/share/plymouth/themes/nexusos
  mkdir -p $theme
  # The spinner, password box, lock and bullets from Plymouth's spinner theme.
  cp ${plymouth}/share/plymouth/themes/spinner/*.png $theme/
  chmod u+w $theme/*.png
  rm -f $theme/watermark.png
  rsvg-convert -w 220 -h 220 ${../branding/nexusos-logo.svg} -o $theme/watermark.png
  cat > $theme/nexusos.plymouth <<EOF
  [Plymouth Theme]
  Name=NexusOS
  Description=NexusOS start-up: the Nexus core on deep space
  ModuleName=two-step

  [two-step]
  Font=DejaVu Sans 12
  TitleFont=DejaVu Sans 22
  ImageDir=$theme
  WatermarkHorizontalAlignment=.5
  WatermarkVerticalAlignment=.4
  DialogHorizontalAlignment=.5
  DialogVerticalAlignment=.68
  TitleHorizontalAlignment=.5
  TitleVerticalAlignment=.68
  HorizontalAlignment=.5
  VerticalAlignment=.8
  Transition=none
  TransitionDuration=0.0
  BackgroundStartColor=0x0B1024
  BackgroundEndColor=0x03050C
  ProgressBarBackgroundColor=0x2A3A66
  ProgressBarForegroundColor=0x7C6BFF
  MessageBelowAnimation=true

  [boot-up]
  UseEndAnimation=false

  [shutdown]
  UseEndAnimation=false

  [reboot]
  UseEndAnimation=false

  [updates]
  SuppressMessages=true
  ProgressBarShowPercentComplete=true
  UseProgressBar=true
  Title=Installing updates…
  SubTitle=Do not turn off your computer
  EOF
  sed -i 's/^  //' $theme/nexusos.plymouth
''
