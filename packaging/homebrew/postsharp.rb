cask "postsharp" do
  version "1.11.21"
  sha256 "95eccffd0b5ea9f33a5e71ada3885b16540f6c168970ce03a579f62080f775ff"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end