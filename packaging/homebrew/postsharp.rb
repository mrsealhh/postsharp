cask "postsharp" do
  version "1.11.8"
  sha256 "8df75ce48055ef770fbdb5780ade8bfd45bee11839872a64b42909d7cdb03f12"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end