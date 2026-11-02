cask "postsharp" do
  version "1.11.13"
  sha256 "7cb015c4bc354497c42c6751327f1b836a64c5f4531f4414a8b537a6bb8a7177"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end