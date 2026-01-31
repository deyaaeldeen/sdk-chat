# VHS tape file for SDK Chat demo
# https://github.com/charmbracelet/vhs

Output docs/demo.gif
Set Shell "bash"
Set FontSize 14
Set Width 900
Set Height 500
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 40ms

# Reset and install (hidden)
Hide
Type "export DOTNET_ROOT=$HOME/.dotnet"
Enter
Sleep 300ms
Type "dotnet tool uninstall -g microsoft.sdkchat 2>/dev/null || true"
Enter
Sleep 1s
Type "cd /tmp && rm -rf sdk-chat"
Enter
Sleep 300ms
Type "git clone https://github.com/deyaaeldeen/sdk-chat && cd sdk-chat"
Enter
Sleep 3s
Type "dotnet pack Microsoft.SdkChat -o ./nupkg >/dev/null 2>&1"
Enter
Sleep 10s
Type "dotnet tool install -g --add-source ./nupkg Microsoft.SdkChat >/dev/null 2>&1"
Enter
Sleep 3s
Type "clear"
Enter
Sleep 300ms
Show

# Generate a sample
Type "sdk-chat package sample generate ~/openai-dotnet --count 1"
Sleep 300ms
Enter
Sleep 25s
