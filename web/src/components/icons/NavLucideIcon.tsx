import {
  Home, LayoutDashboard, Menu as MenuIcon, Compass,
  User, Users, UserCog, Shield, Lock, Key,
  Database, Table, FileText, File, Folder, FolderOpen, Files,
  BarChart3, LineChart, PieChart, TrendingUp, Activity,
  Bell, Mail, MessageSquare, Phone,
  Calendar, Clock,
  Plus, Edit, Trash2, Save, Download, Upload, Search, Filter,
  Eye, AlertCircle, AlertTriangle, Info, CheckCircle, HelpCircle,
  Briefcase, Building2, MapPin, Globe,
  Wrench, Settings, Tag, Bookmark, Star,
  Box,
} from 'lucide-react'
import type { LucideProps } from 'lucide-react'
import type { ComponentType } from 'react'

// Story 4.7 — tree-shaken Lucide icons for the public navbar bundle. Unlike
// LucideIcon.tsx (which uses `import * as Icons` and pulls the full ~3,000 icon
// set), this component imports each icon by name. Only icons listed in ICON_MAP
// land in the public bundle.
//
// Keep in sync with COMMON_LUCIDE_ICONS in IconPickerSection — any icon the
// admin can pick MUST also be importable here, otherwise the navbar will fall
// back to the Box placeholder despite the menu having a valid icon saved.
const ICON_MAP: Record<string, ComponentType<LucideProps>> = {
  Home, LayoutDashboard, Menu: MenuIcon, Compass,
  User, Users, UserCog, Shield, Lock, Key,
  Database, Table, FileText, File, Folder, FolderOpen, Files,
  BarChart3, LineChart, PieChart, TrendingUp, Activity,
  Bell, Mail, MessageSquare, Phone,
  Calendar, Clock,
  Plus, Edit, Trash2, Save, Download, Upload, Search, Filter,
  Eye, AlertCircle, AlertTriangle, Info, CheckCircle, HelpCircle,
  Briefcase, Building2, MapPin, Globe,
  Wrench, Settings, Tag, Bookmark, Star,
  Box,
}

interface NavLucideIconProps extends Omit<LucideProps, 'ref'> {
  name: string
}

// Unknown names fall back to a generic Box placeholder so a menu with an
// unrecognized icon doesn't render as `null` in the navbar.
export function NavLucideIcon({ name, size = 16, ...rest }: NavLucideIconProps) {
  const IconComponent = ICON_MAP[name] ?? Box
  return <IconComponent size={size} {...rest} />
}
