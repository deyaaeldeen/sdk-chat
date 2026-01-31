# VHS tape file for SDK Chat demo
# https://github.com/charmbracelet/vhs

Output demo/demo.gif
Set Shell "bash"
Set FontSize 14
Set Width 900
Set Height 500
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 40ms

# Generate a sample
Type "sdk-chat package sample generate ~/openai-dotnet --use-openai --count 1"
Sleep 300ms
Enter
Sleep 25s
