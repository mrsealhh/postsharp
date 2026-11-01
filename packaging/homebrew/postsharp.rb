cask "postsharp" do
  version "1.11.9"
  sha256 "7a20732158048e66bea6e93152471ad4b0fcdcef504f58c6773d75a7fd3b6c9f"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end