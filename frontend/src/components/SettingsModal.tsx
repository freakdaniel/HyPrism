import React, { useState, useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { X, Github, Bug, Check, AlertTriangle, ChevronDown, ExternalLink, Power, FolderOpen, Trash2, Settings, Database, Globe, Code, Image, User, Edit3, Shuffle, Copy, CheckCircle, Palette, Monitor } from 'lucide-react';
import { BrowserOpenURL } from '../../wailsjs/runtime/runtime';
import { 
    GetCloseAfterLaunch, 
    SetCloseAfterLaunch, 
    OpenLauncherFolder,
    DeleteLauncherData,
    GetLauncherFolderPath,
    GetCustomInstanceDir,
    SetInstanceDirectory,
    BrowseFolder,
    GetDisableNews,
    SetDisableNews,
    GetBackgroundMode,
    SetBackgroundMode,
    GetLauncherDataDirectory,
    SetLauncherDataDirectory,
    GetDisableHardwareAcceleration,
    SetDisableHardwareAcceleration,
    GetNick,
    SetNick,
    GetUUID,
    SetUUID,
    GetAuthDomain
} from '../../wailsjs/go/app/App';
import { useAccentColor } from '../contexts/AccentColorContext';
import { DiscordIcon } from './DiscordIcon';
import { Language } from '../constants/enums';
import { LANGUAGE_CONFIG } from '../constants/languages';
import appIcon from '../assets/appicon.png';
import { SkinCustomizer } from './SkinCustomizer';

// Import background images for previews
const backgroundModulesJpg = import.meta.glob('../assets/bg_*.jpg', { query: '?url', import: 'default', eager: true });
const backgroundModulesPng = import.meta.glob('../assets/bg_*.png', { query: '?url', import: 'default', eager: true });
const allBackgrounds = { ...backgroundModulesJpg, ...backgroundModulesPng };
const backgroundImages = Object.entries(allBackgrounds)
  .sort(([a], [b]) => {
    const numA = parseInt(a.match(/bg_(\d+)/)?.[1] || '0');
    const numB = parseInt(b.match(/bg_(\d+)/)?.[1] || '0');
    return numA - numB;
  })
  .map(([path, url]) => ({ 
    name: path.match(/bg_(\d+)/)?.[0] || 'bg_1', 
    url: url as string 
  }));

// Color palette for accent colors (12 colors)
const ACCENT_COLORS = [
    '#FFA845', // Orange (default)
    '#FF6B6B', // Red
    '#FF85C0', // Pink
    '#B37FEB', // Purple
    '#5C7CFA', // Blue
    '#339AF0', // Light Blue
    '#22B8CF', // Cyan
    '#20C997', // Teal
    '#51CF66', // Green
    '#94D82D', // Lime
    '#FCC419', // Yellow
    '#FFFFFF', // White
];

// Solid color wallpapers (36 colors)
const SOLID_COLORS = [
    '#0a0a0a', '#1a1a1a', '#2a2a2a', '#3a3a3a', '#4a4a4a', '#5a5a5a',
    '#1a0000', '#2a0000', '#3a1010', '#4a2020', '#5a3030', '#6a4040',
    '#001a00', '#002a00', '#103a10', '#204a20', '#305a30', '#406a40',
    '#00001a', '#00002a', '#10103a', '#20204a', '#30305a', '#40406a',
    '#1a1a00', '#2a2a00', '#3a3a10', '#4a4a20', '#5a5a30', '#6a6a40',
    '#1a001a', '#2a002a', '#3a103a', '#4a204a', '#5a305a', '#6a406a',
];

interface Contributor {
    login: string;
    avatar_url: string;
    html_url: string;
    contributions: number;
}

interface SettingsModalProps {
    onClose: () => void;
    launcherBranch: string;
    onLauncherBranchChange: (branch: string) => void;
    onShowModManager?: (query?: string) => void;
    rosettaWarning?: { message: string; command: string; tutorialUrl?: string } | null;
    onBackgroundModeChange?: (mode: string) => void;
    onNewsDisabledChange?: (disabled: boolean) => void;
    onAccentColorChange?: (color: string) => void;
}

type SettingsTab = 'profile' | 'general' | 'visual' | 'data' | 'about' | 'developer';

// Auth server base URL for avatar/skin head
const DEFAULT_AUTH_DOMAIN = 'sessions.sanasol.ws';

export const SettingsModal: React.FC<SettingsModalProps> = ({
    onClose,
    launcherBranch,
    onLauncherBranchChange,
    onShowModManager,
    rosettaWarning,
    onBackgroundModeChange,
    onNewsDisabledChange,
    onAccentColorChange
}) => {
    const { i18n, t } = useTranslation();
    const [activeTab, setActiveTab] = useState<SettingsTab>('profile');
    const [isLanguageOpen, setIsLanguageOpen] = useState(false);
    const [isBranchOpen, setIsBranchOpen] = useState(false);
    const [showTranslationConfirm, setShowTranslationConfirm] = useState<{ langName: string; langCode: string; searchQuery: string } | null>(null);
    const [dontAskAgain, setDontAskAgain] = useState(false);
    const [selectedLauncherBranch, setSelectedLauncherBranch] = useState(launcherBranch);
    const [closeAfterLaunch, setCloseAfterLaunch] = useState(false);
    const [launcherFolderPath, setLauncherFolderPath] = useState('');
    const [instanceDir, setInstanceDir] = useState('');
    const [devModeEnabled, setDevModeEnabled] = useState(false);
    const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
    const [disableNews, setDisableNews] = useState(false);
    const [backgroundMode, setBackgroundModeState] = useState('slideshow');
    const [showAllBackgrounds, setShowAllBackgrounds] = useState(false);
    const [launcherDataDir, setLauncherDataDir] = useState('');
    const [disableHardwareAcceleration, setDisableHardwareAcceleration] = useState(false);
    const { accentColor, setAccentColor: setAccentColorContext } = useAccentColor();
    const [contributors, setContributors] = useState<Contributor[]>([]);
    const [isLoadingContributors, setIsLoadingContributors] = useState(false);
    const languageDropdownRef = useRef<HTMLDivElement>(null);
    const branchDropdownRef = useRef<HTMLDivElement>(null);

    // Profile state
    const [profileUsername, setProfileUsername] = useState('');
    const [profileUuid, setProfileUuid] = useState('');
    const [editingUsername, setEditingUsername] = useState(false);
    const [editingUuid, setEditingUuid] = useState(false);
    const [editUsernameValue, setEditUsernameValue] = useState('');
    const [editUuidValue, setEditUuidValue] = useState('');
    const [copiedUuid, setCopiedUuid] = useState(false);
    const [authDomain, setAuthDomain] = useState('sessions.sanasol.ws');

    const [showSkinCustomizer, setShowSkinCustomizer] = useState(false);

    useEffect(() => {
        const loadSettings = async () => {
            try {
                const closeAfter = await GetCloseAfterLaunch();
                setCloseAfterLaunch(closeAfter);
                
                const folderPath = await GetLauncherFolderPath();
                setLauncherFolderPath(folderPath);
                
                const customDir = await GetCustomInstanceDir();
                setInstanceDir(customDir || folderPath); // Show real path
                
                const newsDisabled = await GetDisableNews();
                setDisableNews(newsDisabled);
                
                const bgMode = await GetBackgroundMode();
                setBackgroundModeState(bgMode);
                
                const dataDir = await GetLauncherDataDirectory();
                setLauncherDataDir(dataDir || folderPath); // Show real path
                
                const hwAccelDisabled = await GetDisableHardwareAcceleration();
                setDisableHardwareAcceleration(hwAccelDisabled);
                
                // Load profile data
                const username = await GetNick();
                // Use 'HyPrism' as fallback - matches backend default
                const displayName = username || 'HyPrism';
                setProfileUsername(displayName);
                setEditUsernameValue(displayName);
                
                const uuid = await GetUUID();
                setProfileUuid(uuid || '');
                setEditUuidValue(uuid || '');
                
                // Load auth domain for profile pictures
                const domain = await GetAuthDomain();
                if (domain) setAuthDomain(domain);
                
                // Accent color is now handled by AccentColorContext
                
                const savedDevMode = localStorage.getItem('hyprism_dev_mode');
                setDevModeEnabled(savedDevMode === 'true');
            } catch (err) {
                console.error('Failed to load settings:', err);
            }
        };
        loadSettings();
    }, []);

    // Load contributors when About tab is active
    useEffect(() => {
        if (activeTab === 'about' && contributors.length === 0 && !isLoadingContributors) {
            setIsLoadingContributors(true);
            fetch('https://api.github.com/repos/yyyumeniku/HyPrism/contributors')
                .then(res => res.json())
                .then(data => {
                    if (Array.isArray(data)) {
                        setContributors(data);
                    }
                })
                .catch(err => console.error('Failed to load contributors:', err))
                .finally(() => setIsLoadingContributors(false));
        }
    }, [activeTab, contributors.length, isLoadingContributors]);

    useEffect(() => {
        const handleClickOutside = (e: MouseEvent) => {
            if (languageDropdownRef.current && !languageDropdownRef.current.contains(e.target as Node)) {
                setIsLanguageOpen(false);
            }
            if (branchDropdownRef.current && !branchDropdownRef.current.contains(e.target as Node)) {
                setIsBranchOpen(false);
            }
        };

        const handleEscape = (e: KeyboardEvent) => {
            if (e.key === 'Escape') {
                if (showTranslationConfirm) {
                    setShowTranslationConfirm(null);
                } else if (showAllBackgrounds) {
                    setShowAllBackgrounds(false);
                } else {
                    onClose();
                }
            }
        };

        document.addEventListener('mousedown', handleClickOutside);
        document.addEventListener('keydown', handleEscape);
        return () => {
            document.removeEventListener('mousedown', handleClickOutside);
            document.removeEventListener('keydown', handleEscape);
        };
    }, [onClose, showTranslationConfirm, showAllBackgrounds]);

    const handleLanguageSelect = (langCode: Language) => {
        i18n.changeLanguage(langCode);
        setIsLanguageOpen(false);

        if (localStorage.getItem('suppressTranslationPrompt') === 'true') {
            return;
        }

        if (langCode !== Language.ENGLISH && onShowModManager) {
            const langConfig = LANGUAGE_CONFIG[langCode];
            if (langConfig) {
                setDontAskAgain(false);
                setShowTranslationConfirm({
                    langName: langConfig.nativeName,
                    langCode: langCode,
                    searchQuery: langConfig.searchQuery
                });
            }
        }
    };

    const handleTranslationConfirm = () => {
        if (showTranslationConfirm && onShowModManager) {
            if (dontAskAgain) {
                localStorage.setItem('suppressTranslationPrompt', 'true');
            }
            onShowModManager(showTranslationConfirm.searchQuery);
        }
        setShowTranslationConfirm(null);
    };

    const handleTranslationDismiss = () => {
        if (dontAskAgain) {
            localStorage.setItem('suppressTranslationPrompt', 'true');
        }
        setShowTranslationConfirm(null);
    };

    const handleLauncherBranchChange = async (branch: string) => {
        setSelectedLauncherBranch(branch);
        setIsBranchOpen(false);
        onLauncherBranchChange(branch);
    };

    const handleCloseAfterLaunchChange = async () => {
        const newValue = !closeAfterLaunch;
        setCloseAfterLaunch(newValue);
        await SetCloseAfterLaunch(newValue);
    };

    const handleOpenLauncherFolder = async () => {
        try {
            await OpenLauncherFolder();
        } catch (err) {
            console.error('Failed to open launcher folder:', err);
            if (launcherFolderPath) {
                BrowserOpenURL(`file://${encodeURI(launcherFolderPath)}`);
            }
        }
    };

    const handleDeleteLauncherData = async () => {
        const success = await DeleteLauncherData();
        if (success) {
            setShowDeleteConfirm(false);
            onClose();
        }
    };

    const handleBrowseInstanceDir = async () => {
        try {
            const selectedPath = await BrowseFolder(instanceDir || launcherFolderPath);
            if (selectedPath) {
                setInstanceDir(selectedPath);
                await SetInstanceDirectory(selectedPath);
            }
        } catch (err) {
            console.error('Failed to set instance directory:', err);
        }
    };

    const handleDisableNewsChange = async (disabled: boolean) => {
        setDisableNews(disabled);
        try {
            await SetDisableNews(disabled);
            onNewsDisabledChange?.(disabled);
        } catch (err) {
            console.error('Failed to set news setting:', err);
        }
    };

    const handleBackgroundModeChange = async (mode: string) => {
        setBackgroundModeState(mode);
        try {
            await SetBackgroundMode(mode);
            onBackgroundModeChange?.(mode);
        } catch (err) {
            console.error('Failed to set background mode:', err);
        }
    };

    const handleBrowseLauncherDataDir = async () => {
        try {
            const selectedPath = await BrowseFolder(launcherDataDir || launcherFolderPath);
            if (selectedPath) {
                setLauncherDataDir(selectedPath);
                await SetLauncherDataDirectory(selectedPath);
            }
        } catch (err) {
            console.error('Failed to browse folder:', err);
        }
    };

    const handleDisableHardwareAccelerationChange = async (disabled: boolean) => {
        setDisableHardwareAcceleration(disabled);
        try {
            await SetDisableHardwareAcceleration(disabled);
        } catch (err) {
            console.error('Failed to set hardware acceleration setting:', err);
        }
    };

    const handleAccentColorChange = async (color: string) => {
        // Update the global context (which also saves to backend)
        await setAccentColorContext(color);
        onAccentColorChange?.(color);
    };

    const handleDevModeToggle = () => {
        const newValue = !devModeEnabled;
        setDevModeEnabled(newValue);
        localStorage.setItem('hyprism_dev_mode', newValue ? 'true' : 'false');
    };

    const openGitHub = () => BrowserOpenURL('https://github.com/yyyumeniku/HyPrism');
    const openBugReport = () => BrowserOpenURL('https://github.com/yyyumeniku/HyPrism/issues/new');
    const openDiscord = () => BrowserOpenURL('https://discord.gg/3U8KNbap3g');

    const currentLangConfig = LANGUAGE_CONFIG[i18n.language as Language] || LANGUAGE_CONFIG[Language.ENGLISH];

    const tabs = [
        { id: 'profile' as const, icon: User, label: t('Profile') },
        { id: 'general' as const, icon: Settings, label: t('General') },
        { id: 'visual' as const, icon: Image, label: t('Visual') },
        { id: 'data' as const, icon: Database, label: t('Data') },
        { id: 'about' as const, icon: Globe, label: t('About') },
        ...(devModeEnabled ? [{ id: 'developer' as const, icon: Code, label: t('Developer') }] : []),
    ];

    // Profile helpers
    const generateUUID = () => {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
            const r = (Math.random() * 16) | 0;
            const v = c === 'x' ? r : (r & 0x3) | 0x8;
            return v.toString(16);
        });
    };

    const handleSaveUsername = async () => {
        if (editUsernameValue.trim() && editUsernameValue.length <= 16) {
            try {
                await SetNick(editUsernameValue.trim());
                setProfileUsername(editUsernameValue.trim());
                setEditingUsername(false);
            } catch (err) {
                console.error('Failed to save username:', err);
            }
        }
    };

    const handleSaveUuid = async () => {
        const trimmed = editUuidValue.trim();
        if (trimmed) {
            try {
                await SetUUID(trimmed);
                setProfileUuid(trimmed);
                setEditingUuid(false);
            } catch (err) {
                console.error('Failed to save UUID:', err);
            }
        }
    };

    const handleRandomizeUuid = () => {
        const newUuid = generateUUID();
        setEditUuidValue(newUuid);
    };

    const handleCopyUuid = async () => {
        try {
            await navigator.clipboard.writeText(profileUuid);
            setCopiedUuid(true);
            setTimeout(() => setCopiedUuid(false), 2000);
        } catch (err) {
            console.error('Failed to copy UUID:', err);
        }
    };

    // Get avatar head URL from auth server
    const getAvatarHeadUrl = (uuid: string) => {
        const domain = authDomain || DEFAULT_AUTH_DOMAIN;
        return `https://${domain}/avatar/${uuid}/head?bg=transparent`;
    };

    const truncateName = (name: string, maxLength: number = 10) => {
        if (name.length <= maxLength) return name;
        return name.slice(0, maxLength - 2) + '...';
    };

    // Get the maintainer (yyyumeniku)
    const maintainer = contributors.find(c => c.login.toLowerCase() === 'yyyumeniku');
    const otherContributors = contributors.filter(c => c.login.toLowerCase() !== 'yyyumeniku');

    return (
        <>
            <div className="fixed inset-0 z-[200] flex items-center justify-center bg-black/60 backdrop-blur-sm">
                <div className="bg-[#1a1a1a] border border-white/10 rounded-2xl shadow-2xl flex overflow-hidden max-w-3xl w-full mx-4 max-h-[80vh]">
                    {/* Sidebar */}
                    <div className="w-48 bg-[#151515] border-r border-white/5 flex flex-col py-4">
                        <h2 className="text-lg font-bold text-white px-4 mb-4">{t('Settings')}</h2>
                        <nav className="flex-1 space-y-1 px-2">
                            {tabs.map((tab) => (
                                <button
                                    key={tab.id}
                                    onClick={() => setActiveTab(tab.id)}
                                    className={`w-full flex items-center gap-3 px-3 py-2.5 rounded-lg text-sm transition-colors ${
                                        activeTab === tab.id
                                            ? 'bg-white/10 text-white'
                                            : 'text-white/60 hover:text-white hover:bg-white/5'
                                    }`}
                                    style={activeTab === tab.id ? { backgroundColor: `${accentColor}20`, color: accentColor } : {}}
                                >
                                    <tab.icon size={18} />
                                    <span>{tab.label}</span>
                                </button>
                            ))}
                        </nav>
                        {/* Dev Mode Toggle at bottom */}
                        <div className="px-2 pt-4 border-t border-white/5 mx-2">
                            <div 
                                className="flex items-center justify-between px-3 py-2 rounded-lg cursor-pointer hover:bg-white/5 transition-colors"
                                onClick={handleDevModeToggle}
                            >
                                <div className="flex items-center gap-2 text-white/40">
                                    <Code size={14} />
                                    <span className="text-xs">{t('Dev Mode')}</span>
                                </div>
                                <div 
                                    className={`w-8 h-4 rounded-full flex items-center transition-colors`}
                                    style={{ backgroundColor: devModeEnabled ? accentColor : 'rgba(255,255,255,0.2)' }}
                                >
                                    <div className={`w-3 h-3 rounded-full bg-white shadow-md transform transition-transform ${devModeEnabled ? 'translate-x-4' : 'translate-x-0.5'}`} />
                                </div>
                            </div>
                        </div>
                    </div>

                    {/* Content */}
                    <div className="flex-1 flex flex-col min-w-0">
                        {/* Header */}
                        <div className="flex items-center justify-between p-4 border-b border-white/5">
                            <h3 className="text-white font-medium">{tabs.find(t => t.id === activeTab)?.label}</h3>
                            <button
                                onClick={onClose}
                                className="p-2 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/10 transition-colors"
                            >
                                <X size={20} />
                            </button>
                        </div>

                        {/* Scrollable Content */}
                        <div className="flex-1 overflow-y-auto p-6 space-y-6">
                            {/* Rosetta Warning (shown on all tabs) */}
                            {rosettaWarning && (
                                <div className="p-4 bg-yellow-500/10 border border-yellow-500/30 rounded-xl">
                                    <div className="flex items-start gap-3">
                                        <AlertTriangle size={20} className="text-yellow-500 flex-shrink-0 mt-0.5" />
                                        <div className="flex-1">
                                            <p className="text-yellow-500 text-sm font-medium mb-2">{rosettaWarning.message}</p>
                                            <div className="flex flex-col gap-2">
                                                <code className="text-xs text-white/70 bg-black/30 px-2 py-1 rounded font-mono break-all">
                                                    {rosettaWarning.command}
                                                </code>
                                                {rosettaWarning.tutorialUrl && (
                                                    <button
                                                        onClick={() => BrowserOpenURL(rosettaWarning.tutorialUrl!)}
                                                        className="flex items-center gap-1 text-xs hover:opacity-80 transition-colors w-fit"
                                                        style={{ color: accentColor }}
                                                    >
                                                        <ExternalLink size={12} />
                                                        {t('Watch Tutorial')}
                                                    </button>
                                                )}
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            )}

                            {/* Profile Tab */}
                            {activeTab === 'profile' && (
                                <div className="space-y-6">
                                    {/* Profile Picture and Username */}
                                    <div className="flex flex-col items-center gap-4 py-4">
                                        {/* Skin Head from Auth Server */}
                                        <div 
                                            className="w-24 h-24 rounded-full overflow-hidden border-2 cursor-pointer hover:scale-105 transition-transform bg-[#151515]"
                                            style={{ borderColor: accentColor }}
                                            title={t('Your character head from the skin customizer')}
                                        >
                                            {profileUuid ? (
                                                <iframe
                                                    src={getAvatarHeadUrl(profileUuid)}
                                                    width="96"
                                                    height="96"
                                                    frameBorder="0"
                                                    className="w-full h-full"
                                                    title="Player Avatar Head"
                                                    loading="lazy"
                                                />
                                            ) : (
                                                <div className="w-full h-full flex items-center justify-center text-white/30">
                                                    <User size={40} />
                                                </div>
                                            )}
                                        </div>
                                        
                                        {/* Username */}
                                        <div className="flex items-center gap-2">
                                            {editingUsername ? (
                                                <div className="flex items-center gap-2">
                                                    <input
                                                        type="text"
                                                        value={editUsernameValue}
                                                        onChange={(e) => setEditUsernameValue(e.target.value)}
                                                        onKeyDown={(e) => {
                                                            if (e.key === 'Enter') handleSaveUsername();
                                                            if (e.key === 'Escape') {
                                                                setEditUsernameValue(profileUsername);
                                                                setEditingUsername(false);
                                                            }
                                                        }}
                                                        maxLength={16}
                                                        autoFocus
                                                        className="bg-[#151515] text-white text-xl font-bold px-3 py-1 rounded-lg border outline-none w-40 text-center"
                                                        style={{ borderColor: accentColor }}
                                                    />
                                                    <button
                                                        onClick={handleSaveUsername}
                                                        className="p-2 rounded-lg hover:opacity-80"
                                                        style={{ backgroundColor: `${accentColor}33`, color: accentColor }}
                                                    >
                                                        <Check size={16} />
                                                    </button>
                                                </div>
                                            ) : (
                                                <>
                                                    <span className="text-2xl font-bold text-white">{profileUsername}</span>
                                                    <button
                                                        onClick={() => {
                                                            setEditUsernameValue(profileUsername);
                                                            setEditingUsername(true);
                                                        }}
                                                        className="p-1.5 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/5"
                                                        title={t('Edit Username')}
                                                    >
                                                        <Edit3 size={14} />
                                                    </button>
                                                </>
                                            )}
                                        </div>
                                    </div>

                                    {/* Skin Customizer Button - Opens In-App */}
                                    <div className="p-4 rounded-xl bg-[#151515] border border-white/10">
                                        <label className="block text-sm text-white/60 mb-3">{t('Character Appearance')}</label>
                                        <p className="text-xs text-white/40 mb-3">
                                            {t('Customize your character\'s skin, hair, clothes, and accessories.')}
                                        </p>
                                        <button
                                            onClick={() => setShowSkinCustomizer(true)}
                                            disabled={!profileUuid}
                                            className="w-full h-12 px-4 rounded-xl flex items-center justify-center gap-2 text-white font-medium transition-all hover:scale-[1.02] active:scale-[0.98] disabled:opacity-50 disabled:cursor-not-allowed"
                                            style={{ backgroundColor: accentColor }}
                                        >
                                            <Palette size={18} />
                                            {t('Open Skin Customizer')}
                                        </button>
                                    </div>

                                    {/* UUID Section */}
                                    <div className="p-4 rounded-xl bg-[#151515] border border-white/10">
                                        <div className="flex items-center justify-between mb-2">
                                            <label className="text-sm text-white/60">{t('Player UUID')}</label>
                                            <div className="flex items-center gap-1">
                                                <button
                                                    onClick={handleCopyUuid}
                                                    className="p-1.5 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/10"
                                                    title={t('Copy UUID')}
                                                >
                                                    {copiedUuid ? <CheckCircle size={14} className="text-green-400" /> : <Copy size={14} />}
                                                </button>
                                                <button
                                                    onClick={() => {
                                                        setEditUuidValue(profileUuid);
                                                        setEditingUuid(true);
                                                    }}
                                                    className="p-1.5 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/10"
                                                    title={t('Edit UUID')}
                                                >
                                                    <Edit3 size={14} />
                                                </button>
                                            </div>
                                        </div>
                                        
                                        {editingUuid ? (
                                            <div className="flex items-center gap-2">
                                                <input
                                                    type="text"
                                                    value={editUuidValue}
                                                    onChange={(e) => setEditUuidValue(e.target.value)}
                                                    onKeyDown={(e) => {
                                                        if (e.key === 'Enter') handleSaveUuid();
                                                        if (e.key === 'Escape') {
                                                            setEditUuidValue(profileUuid);
                                                            setEditingUuid(false);
                                                        }
                                                    }}
                                                    autoFocus
                                                    className="flex-1 bg-[#0a0a0a] text-white font-mono text-sm px-3 py-2 rounded-lg border outline-none"
                                                    style={{ borderColor: accentColor }}
                                                />
                                                <button
                                                    onClick={handleRandomizeUuid}
                                                    className="p-2 rounded-lg bg-white/10 text-white/70 hover:bg-white/20"
                                                    title={t('Generate Random UUID')}
                                                >
                                                    <Shuffle size={16} />
                                                </button>
                                                <button
                                                    onClick={handleSaveUuid}
                                                    className="p-2 rounded-lg hover:opacity-80"
                                                    style={{ backgroundColor: `${accentColor}33`, color: accentColor }}
                                                    title={t('Save UUID')}
                                                >
                                                    <Check size={16} />
                                                </button>
                                            </div>
                                        ) : (
                                            <p className="text-white font-mono text-sm truncate">{profileUuid || 'No UUID set'}</p>
                                        )}
                                    </div>
                                </div>
                            )}

                            {/* General Tab */}
                            {activeTab === 'general' && (
                                <div className="space-y-6">
                                    {/* Language Selector */}
                                    <div>
                                        <label className="block text-sm text-white/60 mb-2">{t('Language')}</label>
                                        <div ref={languageDropdownRef} className="relative">
                                            <button
                                                onClick={() => {
                                                    setIsLanguageOpen(!isLanguageOpen);
                                                    setIsBranchOpen(false);
                                                }}
                                                className="w-full h-12 px-4 rounded-xl bg-[#151515] border border-white/10 flex items-center justify-between text-white transition-colors"
                                                style={{ borderColor: isLanguageOpen ? `${accentColor}50` : undefined }}
                                            >
                                                <div className="flex items-center gap-2">
                                                    <span className="font-medium">{currentLangConfig.nativeName}</span>
                                                    <span className="text-white/50 text-sm">({currentLangConfig.name})</span>
                                                </div>
                                                <ChevronDown size={16} className={`text-white/40 transition-transform ${isLanguageOpen ? 'rotate-180' : ''}`} />
                                            </button>

                                            {isLanguageOpen && (
                                                <div className="absolute top-full left-0 right-0 mt-2 z-10 max-h-60 overflow-y-auto bg-[#1a1a1a] border border-white/10 rounded-xl shadow-xl shadow-black/50">
                                                    {Object.values(LANGUAGE_CONFIG).map((lang) => (
                                                        <button
                                                            key={lang.code}
                                                            onClick={() => handleLanguageSelect(lang.code)}
                                                            className={`w-full px-4 py-3 flex items-center gap-2 text-sm ${i18n.language === lang.code
                                                                ? 'text-white'
                                                                : 'text-white/70 hover:bg-white/10 hover:text-white'
                                                                }`}
                                                            style={i18n.language === lang.code ? { backgroundColor: `${accentColor}20`, color: accentColor } : {}}
                                                        >
                                                            {i18n.language === lang.code && <Check size={14} style={{ color: accentColor }} strokeWidth={3} />}
                                                            <div className={`flex flex-col items-start ${i18n.language === lang.code ? '' : 'ml-[22px]'}`}>
                                                                <span className="font-medium">{lang.nativeName}</span>
                                                                <span className="text-xs opacity-50">{lang.name}</span>
                                                            </div>
                                                        </button>
                                                    ))}
                                                </div>
                                            )}
                                        </div>
                                    </div>

                                    {/* Launcher Branch Selector */}
                                    <div>
                                        <label className="block text-sm text-white/60 mb-2">{t('Update Channel')}</label>
                                        <div ref={branchDropdownRef} className="relative">
                                            <button
                                                onClick={() => {
                                                    setIsBranchOpen(!isBranchOpen);
                                                    setIsLanguageOpen(false);
                                                }}
                                                className="w-full h-12 px-4 rounded-xl bg-[#151515] border border-white/10 flex items-center justify-between text-white transition-colors"
                                                style={{ borderColor: isBranchOpen ? `${accentColor}50` : undefined }}
                                            >
                                                <div className="flex items-center gap-2">
                                                    <span className="font-medium">
                                                        {selectedLauncherBranch === 'beta' ? t('Beta') : t('Stable')}
                                                    </span>
                                                    {selectedLauncherBranch === 'beta' && (
                                                        <span className="text-xs px-2 py-0.5 rounded bg-yellow-500/20 text-yellow-500">
                                                            {t('Experimental')}
                                                        </span>
                                                    )}
                                                </div>
                                                <ChevronDown size={16} className={`text-white/40 transition-transform ${isBranchOpen ? 'rotate-180' : ''}`} />
                                            </button>

                                            {isBranchOpen && (
                                                <div className="absolute top-full left-0 right-0 mt-2 z-10 bg-[#1a1a1a] border border-white/10 rounded-xl shadow-xl shadow-black/50 overflow-hidden">
                                                    <button
                                                        onClick={() => handleLauncherBranchChange('release')}
                                                        className={`w-full px-4 py-3 flex items-center gap-2 text-sm ${selectedLauncherBranch === 'release'
                                                            ? 'text-white'
                                                            : 'text-white/70 hover:bg-white/10 hover:text-white'
                                                            }`}
                                                        style={selectedLauncherBranch === 'release' ? { backgroundColor: `${accentColor}20`, color: accentColor } : {}}
                                                    >
                                                        {selectedLauncherBranch === 'release' && <Check size={14} style={{ color: accentColor }} strokeWidth={3} />}
                                                        <div className={`flex flex-col items-start ${selectedLauncherBranch === 'release' ? '' : 'ml-[22px]'}`}>
                                                            <span className="font-medium">{t('Stable')}</span>
                                                            <span className="text-xs opacity-50">{t('Recommended for most users')}</span>
                                                        </div>
                                                    </button>
                                                    <button
                                                        onClick={() => handleLauncherBranchChange('beta')}
                                                        className={`w-full px-4 py-3 flex items-center gap-2 text-sm ${selectedLauncherBranch === 'beta'
                                                            ? 'text-white'
                                                            : 'text-white/70 hover:bg-white/10 hover:text-white'
                                                            }`}
                                                        style={selectedLauncherBranch === 'beta' ? { backgroundColor: `${accentColor}20`, color: accentColor } : {}}
                                                    >
                                                        {selectedLauncherBranch === 'beta' && <Check size={14} style={{ color: accentColor }} strokeWidth={3} />}
                                                        <div className={`flex flex-col items-start ${selectedLauncherBranch === 'beta' ? '' : 'ml-[22px]'}`}>
                                                            <span className="font-medium">{t('Beta')}</span>
                                                            <span className="text-xs opacity-50">{t('Get early access to new features')}</span>
                                                        </div>
                                                    </button>
                                                </div>
                                            )}
                                        </div>
                                        <p className="mt-2 text-xs text-white/40">
                                            {selectedLauncherBranch === 'beta' 
                                                ? t('You will receive beta updates which may be unstable.')
                                                : t('You will receive stable releases only.')}
                                        </p>
                                    </div>

                                    {/* Toggle Settings */}
                                    <div className="space-y-3">
                                        {/* Close After Launch */}
                                        <div 
                                            className="flex items-center justify-between p-3 rounded-xl bg-[#151515] border border-white/10 cursor-pointer hover:border-white/20 transition-colors"
                                            onClick={handleCloseAfterLaunchChange}
                                        >
                                            <div className="flex items-center gap-3">
                                                <Power size={18} className="text-white/60" />
                                                <div>
                                                    <span className="text-white text-sm">{t('Close after launch')}</span>
                                                    <p className="text-xs text-white/40">{t('Close launcher when game starts')}</p>
                                                </div>
                                            </div>
                                            <div 
                                                className="w-10 h-6 rounded-full flex items-center transition-colors"
                                                style={{ backgroundColor: closeAfterLaunch ? accentColor : 'rgba(255,255,255,0.2)' }}
                                            >
                                                <div className={`w-4 h-4 rounded-full bg-white shadow-md transform transition-transform ${closeAfterLaunch ? 'translate-x-5' : 'translate-x-1'}`} />
                                            </div>
                                        </div>
                                    </div>
                                </div>
                            )}

                            {/* Visual Tab */}
                            {activeTab === 'visual' && (
                                <div className="space-y-6">
                                    {/* Accent Color Chooser */}
                                    <div>
                                        <label className="block text-sm text-white/60 mb-3">{t('Accent Color')}</label>
                                        <div className="flex flex-wrap gap-2">
                                            {ACCENT_COLORS.map((color) => (
                                                <button
                                                    key={color}
                                                    onClick={() => handleAccentColorChange(color)}
                                                    className={`w-8 h-8 rounded-full transition-all ${accentColor === color ? 'ring-2 ring-white ring-offset-2 ring-offset-[#1a1a1a]' : 'hover:scale-110'}`}
                                                    style={{ backgroundColor: color }}
                                                />
                                            ))}
                                        </div>
                                    </div>

                                    {/* Background Chooser */}
                                    <div>
                                        <label className="block text-sm text-white/60 mb-3">{t('Background')}</label>
                                        
                                        {/* Slideshow option */}
                                        <div 
                                            className={`p-3 rounded-xl border cursor-pointer transition-colors mb-3`}
                                            style={{
                                                backgroundColor: backgroundMode === 'slideshow' ? `${accentColor}20` : '#151515',
                                                borderColor: backgroundMode === 'slideshow' ? `${accentColor}50` : 'rgba(255,255,255,0.1)'
                                            }}
                                            onClick={() => handleBackgroundModeChange('slideshow')}
                                        >
                                            <div className="flex items-center gap-3">
                                                <div 
                                                    className="w-5 h-5 rounded-full border-2 flex items-center justify-center"
                                                    style={{ borderColor: backgroundMode === 'slideshow' ? accentColor : 'rgba(255,255,255,0.3)' }}
                                                >
                                                    {backgroundMode === 'slideshow' && <div className="w-2.5 h-2.5 rounded-full" style={{ backgroundColor: accentColor }} />}
                                                </div>
                                                <div>
                                                    <span className="text-white text-sm font-medium">{t('Slideshow')}</span>
                                                    <p className="text-xs text-white/40">{t('Cycle through all backgrounds')}</p>
                                                </div>
                                            </div>
                                        </div>

                                        {/* Background grid - show first 8 */}
                                        <p className="text-xs text-white/40 mb-2">{t('Or choose a static background:')}</p>
                                        <div className="grid grid-cols-4 gap-2">
                                            {backgroundImages.slice(0, 8).map((bg) => (
                                                <div
                                                    key={bg.name}
                                                    className="relative aspect-video rounded-lg overflow-hidden cursor-pointer border-2 transition-all"
                                                    style={{
                                                        borderColor: backgroundMode === bg.name ? accentColor : 'transparent',
                                                        boxShadow: backgroundMode === bg.name ? `0 0 0 2px ${accentColor}30` : 'none'
                                                    }}
                                                    onClick={() => handleBackgroundModeChange(bg.name)}
                                                >
                                                    <img 
                                                        src={bg.url} 
                                                        alt={bg.name}
                                                        className="w-full h-full object-cover"
                                                    />
                                                    {backgroundMode === bg.name && (
                                                        <div className="absolute inset-0 flex items-center justify-center" style={{ backgroundColor: `${accentColor}30` }}>
                                                            <Check size={20} className="text-white drop-shadow-lg" />
                                                        </div>
                                                    )}
                                                </div>
                                            ))}
                                        </div>

                                        {/* Show all button */}
                                        <button
                                            onClick={() => setShowAllBackgrounds(true)}
                                            className="mt-3 w-full h-10 rounded-xl bg-[#151515] border border-white/10 flex items-center justify-center gap-2 text-white/60 hover:text-white hover:border-white/20 transition-colors"
                                        >
                                            <span className="text-sm">{t('Show all backgrounds')}</span>
                                        </button>
                                    </div>

                                    {/* Disable News Toggle */}
                                    <div 
                                        className="flex items-center justify-between p-3 rounded-xl bg-[#151515] border border-white/10 cursor-pointer hover:border-white/20 transition-colors"
                                        onClick={() => handleDisableNewsChange(!disableNews)}
                                    >
                                        <div className="flex items-center gap-3">
                                            <Globe size={18} className="text-white/60" />
                                            <div>
                                                <span className="text-white text-sm">{t('Hide News')}</span>
                                                <p className="text-xs text-white/40">{t('Completely hide the news panel')}</p>
                                            </div>
                                        </div>
                                        <div 
                                            className="w-10 h-6 rounded-full flex items-center transition-colors"
                                            style={{ backgroundColor: disableNews ? accentColor : 'rgba(255,255,255,0.2)' }}
                                        >
                                            <div className={`w-4 h-4 rounded-full bg-white shadow-md transform transition-transform ${disableNews ? 'translate-x-5' : 'translate-x-1'}`} />
                                        </div>
                                    </div>

                                    {/* Disable GPU Acceleration */}
                                    <div 
                                        className="flex items-center justify-between p-3 rounded-xl bg-[#151515] border border-white/10 cursor-pointer hover:border-white/20 transition-colors"
                                        onClick={() => handleDisableHardwareAccelerationChange(!disableHardwareAcceleration)}
                                    >
                                        <div className="flex items-center gap-3">
                                            <Monitor size={18} className="text-white/60" />
                                            <div>
                                                <span className="text-white text-sm">{t('Disable GPU Acceleration')}</span>
                                                <p className="text-xs text-white/40">{t('May help with display issues. Requires restart.')}</p>
                                            </div>
                                        </div>
                                        <div 
                                            className="w-10 h-6 rounded-full flex items-center transition-colors"
                                            style={{ backgroundColor: disableHardwareAcceleration ? accentColor : 'rgba(255,255,255,0.2)' }}
                                        >
                                            <div className={`w-4 h-4 rounded-full bg-white shadow-md transform transition-transform ${disableHardwareAcceleration ? 'translate-x-5' : 'translate-x-1'}`} />
                                        </div>
                                    </div>
                                </div>
                            )}

                            {/* Data Tab */}
                            {activeTab === 'data' && (
                                <div className="space-y-6">
                                    {/* Instance Folder */}
                                    <div>
                                        <label className="block text-sm text-white/60 mb-2">{t('Instance Folder')}</label>
                                        <div className="flex gap-2">
                                            <input
                                                type="text"
                                                value={instanceDir}
                                                readOnly
                                                className="flex-1 h-12 px-4 rounded-xl bg-[#151515] border border-white/10 text-white text-sm focus:outline-none cursor-default"
                                            />
                                            <div className="flex rounded-full overflow-hidden border border-white/10">
                                                <button
                                                    onClick={handleBrowseInstanceDir}
                                                    className="h-12 px-4 bg-[#151515] flex items-center justify-center text-white/60 hover:text-white hover:bg-white/5 transition-colors"
                                                    title={t('Browse')}
                                                >
                                                    <FolderOpen size={18} />
                                                    <span className="ml-2 text-sm">{t('Select')}</span>
                                                </button>
                                                <div className="w-px bg-white/10" />
                                                <button
                                                    onClick={() => BrowserOpenURL(`file://${instanceDir}`)}
                                                    className="h-12 px-4 bg-[#151515] flex items-center justify-center text-white/60 hover:text-white hover:bg-white/5 transition-colors"
                                                    title={t('Open Folder')}
                                                >
                                                    <ExternalLink size={18} />
                                                    <span className="ml-2 text-sm">{t('Open')}</span>
                                                </button>
                                            </div>
                                        </div>
                                    </div>

                                    {/* Launcher Data Folder */}
                                    <div>
                                        <label className="block text-sm text-white/60 mb-2">{t('Launcher Data Folder')}</label>
                                        <div className="flex gap-2">
                                            <input
                                                type="text"
                                                value={launcherDataDir}
                                                readOnly
                                                className="flex-1 h-12 px-4 rounded-xl bg-[#151515] border border-white/10 text-white text-sm focus:outline-none cursor-default"
                                            />
                                            <div className="flex rounded-full overflow-hidden border border-white/10">
                                                <button
                                                    onClick={handleBrowseLauncherDataDir}
                                                    className="h-12 px-4 bg-[#151515] flex items-center justify-center text-white/60 hover:text-white hover:bg-white/5 transition-colors"
                                                    title={t('Browse')}
                                                >
                                                    <FolderOpen size={18} />
                                                    <span className="ml-2 text-sm">{t('Select')}</span>
                                                </button>
                                                <div className="w-px bg-white/10" />
                                                <button
                                                    onClick={() => BrowserOpenURL(`file://${launcherDataDir}`)}
                                                    className="h-12 px-4 bg-[#151515] flex items-center justify-center text-white/60 hover:text-white hover:bg-white/5 transition-colors"
                                                    title={t('Open Folder')}
                                                >
                                                    <ExternalLink size={18} />
                                                    <span className="ml-2 text-sm">{t('Open')}</span>
                                                </button>
                                            </div>
                                        </div>
                                        <p className="mt-2 text-xs text-white/40">{t('Changes take effect after restart.')}</p>
                                    </div>

                                    {/* Launcher Folder Actions */}
                                    <div className="space-y-3">
                                        <button
                                            onClick={handleOpenLauncherFolder}
                                            className="w-full h-12 px-4 rounded-xl bg-[#151515] border border-white/10 flex items-center gap-3 text-white/70 hover:text-white hover:border-white/20 transition-colors"
                                        >
                                            <FolderOpen size={18} />
                                            <span>{t('Open Launcher Folder')}</span>
                                        </button>

                                        <button
                                            onClick={() => setShowDeleteConfirm(true)}
                                            className="w-full h-12 px-4 rounded-xl bg-[#151515] border border-red-500/30 flex items-center gap-3 text-red-400 hover:text-red-300 hover:bg-red-500/10 transition-colors"
                                        >
                                            <Trash2 size={18} />
                                            <span>{t('Delete All Launcher Data')}</span>
                                        </button>
                                    </div>
                                </div>
                            )}

                            {/* About Tab */}
                            {activeTab === 'about' && (
                                <div className="space-y-6">
                                    {/* App Icon and Info - No button styling */}
                                    <div className="flex flex-col items-center py-4">
                                        <img 
                                            src={appIcon} 
                                            alt="HyPrism" 
                                            className="w-20 h-20 mb-3"
                                        />
                                        <h3 className="text-xl font-bold text-white">HyPrism</h3>
                                        <p className="text-sm text-white/50">{t('Unofficial Hytale Launcher')}</p>
                                    </div>

                                    {/* Action Buttons - White icons like main menu */}
                                    <div className="flex justify-center gap-4">
                                        <button
                                            onClick={openGitHub}
                                            className="opacity-80 hover:opacity-100 transition-opacity"
                                            title="GitHub"
                                        >
                                            <Github size={28} className="text-white" />
                                        </button>
                                        <button
                                            onClick={openDiscord}
                                            className="opacity-80 hover:opacity-100 transition-opacity"
                                            title="Discord"
                                        >
                                            <DiscordIcon size={20} color="white" />
                                        </button>
                                        <button
                                            onClick={openBugReport}
                                            className="opacity-80 hover:opacity-100 transition-opacity"
                                            title={t('Bug Report')}
                                        >
                                            <Bug size={28} className="text-white" />
                                        </button>
                                    </div>

                                    {/* Contributors Section - No separator */}
                                    <div className="pt-2">
                                        {isLoadingContributors ? (
                                            <div className="flex justify-center py-4">
                                                <div className="w-6 h-6 border-2 rounded-full animate-spin" style={{ borderColor: `${accentColor}30`, borderTopColor: accentColor }} />
                                            </div>
                                        ) : (
                                            <div className="space-y-4">
                                                {/* Maintainer & Auth Server Creator */}
                                                <div className="flex justify-center gap-4 flex-wrap">
                                                    {maintainer && (
                                                        <button
                                                            onClick={() => BrowserOpenURL(maintainer.html_url)}
                                                            className="flex items-center gap-3 p-2 rounded-lg hover:bg-white/5 transition-colors"
                                                        >
                                                            <img 
                                                                src={maintainer.avatar_url} 
                                                                alt={maintainer.login}
                                                                className="w-12 h-12 rounded-full"
                                                            />
                                                            <div className="text-left">
                                                                <span className="text-white font-medium text-sm">{maintainer.login}</span>
                                                                <p className="text-xs text-white/40">{t('Maintainer & Developer')}</p>
                                                            </div>
                                                        </button>
                                                    )}
                                                    <button
                                                        onClick={() => BrowserOpenURL('https://github.com/sanasol')}
                                                        className="flex items-center gap-3 p-2 rounded-lg hover:bg-white/5 transition-colors"
                                                    >
                                                        <img 
                                                            src="https://avatars.githubusercontent.com/u/1709666?v=4" 
                                                            alt="sanasol"
                                                            className="w-12 h-12 rounded-full"
                                                        />
                                                        <div className="text-left">
                                                            <span className="text-white font-medium text-sm">sanasol</span>
                                                            <p className="text-xs text-white/40">{t('Creator and Maintainer of the Auth Server')}</p>
                                                        </div>
                                                    </button>
                                                </div>

                                                {/* Description */}
                                                <p className="text-xs text-white/40 text-center">{t('Awesome people that helped in the creation of this launcher')}</p>

                                                {/* Other Contributors - 5 per row, larger avatars */}
                                                {otherContributors.length > 0 && (
                                                    <div className="grid grid-cols-5 gap-3 justify-items-center">
                                                        {otherContributors.map((contributor) => (
                                                            <button
                                                                key={contributor.login}
                                                                onClick={() => BrowserOpenURL(contributor.html_url)}
                                                                className="flex flex-col items-center gap-1.5 p-2 rounded-lg hover:bg-white/5 transition-colors w-full"
                                                                title={`${contributor.login} - ${contributor.contributions} contributions`}
                                                            >
                                                                <img 
                                                                    src={contributor.avatar_url} 
                                                                    alt={contributor.login}
                                                                    className="w-12 h-12 rounded-full"
                                                                />
                                                                <span className="text-xs text-white/60 max-w-full truncate text-center">
                                                                    {truncateName(contributor.login, 10)}
                                                                </span>
                                                            </button>
                                                        ))}
                                                    </div>
                                                )}
                                            </div>
                                        )}
                                    </div>

                                    {/* Disclaimer */}
                                    <div className="p-4 rounded-xl bg-[#151515] border border-white/5">
                                        <p className="text-white/50 text-sm text-center">
                                            {t('HyPrism is an unofficial launcher for Hytale. This project is not affiliated with Hypixel Studios.')}
                                        </p>
                                    </div>
                                </div>
                            )}

                            {/* Developer Tab */}
                            {activeTab === 'developer' && devModeEnabled && (
                                <div className="space-y-6">
                                    <div className="p-4 bg-yellow-500/10 border border-yellow-500/30 rounded-xl">
                                        <div className="flex items-center gap-2 text-yellow-500 text-sm font-medium">
                                            <AlertTriangle size={16} />
                                            {t('Developer options are for testing only')}
                                        </div>
                                    </div>

                                    <div className="p-4 rounded-xl bg-[#151515] border border-white/5">
                                        <p className="text-white/40 text-xs">
                                            {t('Debug info:')} Tab={activeTab}, Branch={selectedLauncherBranch}, Accent={accentColor}
                                        </p>
                                    </div>
                                </div>
                            )}
                        </div>
                    </div>
                </div>
            </div>

            {/* Translation Confirmation Modal */}
            {showTranslationConfirm && (
                <div className="fixed inset-0 z-[250] flex items-center justify-center bg-black/60 backdrop-blur-sm">
                    <div className="bg-[#1a1a1a] border border-white/10 rounded-2xl p-6 max-w-md w-full mx-4">
                        <h3 className="text-lg font-bold text-white mb-3">{t('Language Changed')}</h3>
                        <p className="text-white/70 text-sm mb-4">
                            {t('Would you like to search for translation mods for {{language}}?', { language: showTranslationConfirm.langName })}
                        </p>
                        
                        <label className="flex items-center gap-2 mb-4 cursor-pointer">
                            <input
                                type="checkbox"
                                checked={dontAskAgain}
                                onChange={(e) => setDontAskAgain(e.target.checked)}
                                className="w-4 h-4 rounded"
                                style={{ accentColor: accentColor }}
                            />
                            <span className="text-sm text-white/60">{t("Don't ask me again")}</span>
                        </label>

                        <div className="flex gap-3">
                            <button
                                onClick={handleTranslationDismiss}
                                className="flex-1 h-10 rounded-xl bg-white/5 text-white/70 hover:bg-white/10 transition-colors"
                            >
                                {t('No thanks')}
                            </button>
                            <button
                                onClick={handleTranslationConfirm}
                                className="flex-1 h-10 rounded-xl text-black font-medium transition-colors"
                                style={{ backgroundColor: accentColor }}
                            >
                                {t('Search Mods')}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* Delete Confirmation Modal */}
            {showDeleteConfirm && (
                <div className="fixed inset-0 z-[250] flex items-center justify-center bg-black/60 backdrop-blur-sm">
                    <div className="bg-[#1a1a1a] border border-white/10 rounded-2xl p-6 max-w-md w-full mx-4">
                        <div className="flex items-center gap-3 mb-4">
                            <div className="w-10 h-10 rounded-full bg-red-500/20 flex items-center justify-center">
                                <Trash2 size={20} className="text-red-400" />
                            </div>
                            <h3 className="text-lg font-bold text-white">{t('Delete All Data?')}</h3>
                        </div>
                        <p className="text-white/70 text-sm mb-6">
                            {t('This will delete all launcher data including settings, cache, and downloaded files. This action cannot be undone.')}
                        </p>
                        <div className="flex gap-3">
                            <button
                                onClick={() => setShowDeleteConfirm(false)}
                                className="flex-1 h-10 rounded-xl bg-white/5 text-white/70 hover:bg-white/10 transition-colors"
                            >
                                {t('Cancel')}
                            </button>
                            <button
                                onClick={handleDeleteLauncherData}
                                className="flex-1 h-10 rounded-xl bg-red-500 text-white font-medium hover:bg-red-600 transition-colors"
                            >
                                {t('Delete')}
                            </button>
                        </div>
                    </div>
                </div>
            )}

            {/* All Backgrounds Modal - Now includes solid colors */}
            {showAllBackgrounds && (
                <div className="fixed inset-0 z-[250] flex items-center justify-center bg-black/60 backdrop-blur-sm">
                    <div className="bg-[#1a1a1a] border border-white/10 rounded-2xl max-w-2xl w-full mx-4 max-h-[80vh] flex flex-col overflow-hidden">
                        <div className="flex items-center justify-between p-4 border-b border-white/5">
                            <h3 className="text-lg font-bold text-white">{t('Choose Background')}</h3>
                            <button
                                onClick={() => setShowAllBackgrounds(false)}
                                className="p-2 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/10 transition-colors"
                            >
                                <X size={20} />
                            </button>
                        </div>
                        <div className="flex-1 overflow-y-auto p-4">
                            {/* Slideshow option */}
                            <div 
                                className="p-3 rounded-xl border cursor-pointer transition-colors mb-4"
                                style={{
                                    backgroundColor: backgroundMode === 'slideshow' ? `${accentColor}20` : '#151515',
                                    borderColor: backgroundMode === 'slideshow' ? `${accentColor}50` : 'rgba(255,255,255,0.1)'
                                }}
                                onClick={() => handleBackgroundModeChange('slideshow')}
                            >
                                <div className="flex items-center gap-3">
                                    <div 
                                        className="w-5 h-5 rounded-full border-2 flex items-center justify-center"
                                        style={{ borderColor: backgroundMode === 'slideshow' ? accentColor : 'rgba(255,255,255,0.3)' }}
                                    >
                                        {backgroundMode === 'slideshow' && <div className="w-2.5 h-2.5 rounded-full" style={{ backgroundColor: accentColor }} />}
                                    </div>
                                    <div>
                                        <span className="text-white text-sm font-medium">{t('Slideshow')}</span>
                                        <p className="text-xs text-white/40">{t('Cycle through all backgrounds')}</p>
                                    </div>
                                </div>
                            </div>

                            {/* Image backgrounds */}
                            <p className="text-xs text-white/40 mb-2">{t('Images')}</p>
                            <div className="grid grid-cols-4 gap-3 mb-6">
                                {backgroundImages.map((bg) => (
                                    <div
                                        key={bg.name}
                                        className="relative aspect-video rounded-lg overflow-hidden cursor-pointer border-2 transition-all"
                                        style={{
                                            borderColor: backgroundMode === bg.name ? accentColor : 'transparent',
                                            boxShadow: backgroundMode === bg.name ? `0 0 0 2px ${accentColor}30` : 'none'
                                        }}
                                        onClick={() => handleBackgroundModeChange(bg.name)}
                                    >
                                        <img 
                                            src={bg.url} 
                                            alt={bg.name}
                                            className="w-full h-full object-cover"
                                        />
                                        {backgroundMode === bg.name && (
                                            <div className="absolute inset-0 flex items-center justify-center" style={{ backgroundColor: `${accentColor}30` }}>
                                                <Check size={24} className="text-white drop-shadow-lg" />
                                            </div>
                                        )}
                                    </div>
                                ))}
                            </div>

                            {/* Solid colors */}
                            <div className="p-3 rounded-xl bg-[#0f0f0f] border border-white/5">
                            <p className="text-xs text-white/40 mb-2">{t('Solid Colors')}</p>
                            <div className="grid grid-cols-6 gap-2">
                                {SOLID_COLORS.map((color) => (
                                    <div
                                        key={color}
                                        className="aspect-video rounded-lg cursor-pointer border-2 transition-all flex items-center justify-center"
                                        style={{
                                            backgroundColor: color,
                                            borderColor: backgroundMode === `color:${color}` ? accentColor : 'transparent',
                                            boxShadow: backgroundMode === `color:${color}` ? `0 0 0 2px ${accentColor}30` : 'none'
                                        }}
                                        onClick={() => handleBackgroundModeChange(`color:${color}`)}
                                    >
                                        {backgroundMode === `color:${color}` && (
                                            <Check size={16} className="text-white drop-shadow-lg" />
                                        )}
                                    </div>
                                ))}
                            </div>
                            </div>
                        </div>
                    </div>
                </div>
            )}

            {/* Skin Customizer Modal */}
            <SkinCustomizer
                isOpen={showSkinCustomizer}
                onClose={() => setShowSkinCustomizer(false)}
            />
        </>
    );
};
