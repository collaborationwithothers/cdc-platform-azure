variable "argocd_hostname" {
  description = "Public hostname of the Argo CD UI. It is the host half of the OIDC redirect URI registered on the Argo app registration."
  type        = string
  default     = "argocd.consultwithcloud.com"
}
