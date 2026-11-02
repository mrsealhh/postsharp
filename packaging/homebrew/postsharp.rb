cask "postsharp" do
  version "1.11.18"
  sha256 "c985998881c18ba4ab1c8b5ce94b8ae6977711282e66d122bf7bc51e5dc36a5a"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end