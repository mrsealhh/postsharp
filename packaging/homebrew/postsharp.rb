cask "postsharp" do
  version "1.11.3"
  sha256 "3ac0c7b2078953c29750c288ea5c25814e055324b1368bc1633de58a3e811ce5"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end