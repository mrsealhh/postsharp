cask "postsharp" do
  version "1.11.2"
  sha256 "1dd85d42c67eb319baf730daeb9e12ed8dc154803e2bacdf8b66ac312dd0e27a"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end