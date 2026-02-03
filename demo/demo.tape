# VHS tape file for SDK Chat demo
# https://github.com/charmbracelet/vhs

Output demo.gif
Set Shell "bash"
Set FontSize 14
Set Width 900
Set Height 500
Set Theme "Dracula"
Set Padding 20
Set TypingSpeed 40ms

# Show Docker usage (official method)
Type "# SDK Chat - Generate SDK samples with AI"
Enter
Sleep 500ms
Type "# Using Docker (official installation method)"
Enter
Sleep 500ms
Type "./scripts/sdk-chat.sh package samples generate ~/openai-dotnet --count 1"
Sleep 300ms
Enter
Sleep 30s

# Show OpenAI mode with --load-dotenv
Type "# With OpenAI instead of GitHub Copilot"
Enter
Sleep 500ms
Type "./scripts/sdk-chat.sh package samples generate ~/openai-dotnet --use-openai --load-dotenv --count 1"
Sleep 300ms
Enter
Sleep 30s
