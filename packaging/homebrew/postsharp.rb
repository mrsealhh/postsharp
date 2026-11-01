cask "postsharp" do
  version "1.11.5"
  sha256 "b7ae8d41d4ee8eb548094dc5eceb88ddc33492e4d5061ad8fbd6ba798b45a447"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end