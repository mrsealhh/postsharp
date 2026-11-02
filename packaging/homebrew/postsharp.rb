cask "postsharp" do
  version "1.11.14"
  sha256 "73c631410abf2ff2d7db1550e116c9431e40c188191eac0f9d08f2128ea19773"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end