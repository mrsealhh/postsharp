cask "postsharp" do
  version "1.11.4"
  sha256 "08f42592b727369b6d4f61f6287dfdc78c0d21c99d89a7ef59fb6a196c5df13b"

  url "https://github.com/mrsealhh/postsharp/releases/download/v#{version}/PostSharp-osx-arm64.zip"
  name "PostSharp"
  desc "Local API debugging workbench with MCP automation"
  homepage "https://github.com/mrsealhh/postsharp"

  depends_on arch: :arm64

  binary "PostSharp"
end