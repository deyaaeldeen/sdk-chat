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

# Show Copilot mode (default)
Type "# Mode 1: GitHub Copilot (default)"
Enter
Sleep 500ms
Type "sdk-chat package sample generate ~/openai-dotnet --count 1"
Sleep 300ms
Enter
Sleep 25s

# Clear and show OpenAI mode
Type "# Mode 2: OpenAI (load OPENAI_API_KEY from .env)"
Enter
Sleep 500ms
Type "sdk-chat package sample generate ~/openai-dotnet --use-openai --load-dotenv --count 1"
Sleep 300ms
Enter
Sleep 25s
