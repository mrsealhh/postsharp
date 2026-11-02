cask "postsharp" do
  version "1.11.16"
  sha256 "8f3b25b5b1c3e64f9539163436eaa048801e0612b82bfa3259fa953513afe307"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end