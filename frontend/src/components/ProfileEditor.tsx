import React, { useState, useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { X, RefreshCw, Check, User, Edit3, Shuffle, Copy, CheckCircle, ExternalLink } from 'lucide-react';
import { motion, AnimatePresence } from 'framer-motion';
import { useAccentColor } from '../contexts/AccentColorContext';
import { GetUUID, SetUUID, GetNick, SetNick, GetAuthDomain } from '../../wailsjs/go/app/App';
import { BrowserOpenURL } from '../../wailsjs/runtime/runtime';

interface ProfileEditorProps {
    isOpen: boolean;
    onClose: () => void;
    onProfileUpdate?: () => void;
}

// Default auth server domain
const DEFAULT_AUTH_DOMAIN = 'sessions.sanasol.ws';

export const ProfileEditor: React.FC<ProfileEditorProps> = ({ isOpen, onClose, onProfileUpdate }) => {
    const { t } = useTranslation();
    const { accentColor } = useAccentColor();
    const [uuid, setUuid] = useState<string>('');
    const [username, setUsernameState] = useState<string>('');
    const [isLoading, setIsLoading] = useState(true);
    const [isEditingUsername, setIsEditingUsername] = useState(false);
    const [isEditingUuid, setIsEditingUuid] = useState(false);
    const [editUsername, setEditUsername] = useState('');
    const [editUuid, setEditUuid] = useState('');
    const [copiedUuid, setCopiedUuid] = useState(false);
    const [saveStatus, setSaveStatus] = useState<'idle' | 'saving' | 'saved'>('idle');
    const [authDomain, setAuthDomain] = useState(DEFAULT_AUTH_DOMAIN);

    useEffect(() => {
        if (isOpen) {
            loadProfile();
        }
    }, [isOpen]);

    const loadProfile = async () => {
        setIsLoading(true);
        try {
            const [userUuid, userName, domain] = await Promise.all([
                GetUUID(),
                GetNick(),
                GetAuthDomain()
            ]);
            setUuid(userUuid || generateUUID());
            // Use 'HyPrism' as fallback - matches backend default
            const displayName = userName || 'HyPrism';
            setUsernameState(displayName);
            setEditUsername(displayName);
            setEditUuid(userUuid || '');
            if (domain) setAuthDomain(domain);
        } catch (err) {
            console.error('Failed to load profile:', err);
            setUuid(generateUUID());
            setUsernameState('HyPrism');
        }
        setIsLoading(false);
    };

    // Get avatar head URL from auth server
    const getAvatarHeadUrl = () => {
        return `https://${authDomain}/avatar/${uuid}/head?bg=transparent`;
    };

    // Get customizer URL from auth server
    const getCustomizerUrl = () => {
        return `https://${authDomain}/customizer/${uuid}`;
    };

    const generateUUID = () => {
        return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, (c) => {
            const r = (Math.random() * 16) | 0;
            const v = c === 'x' ? r : (r & 0x3) | 0x8;
            return v.toString(16);
        });
    };

    const handleSaveUsername = async () => {
        if (editUsername.trim() && editUsername.length <= 16) {
            setSaveStatus('saving');
            try {
                await SetNick(editUsername.trim());
                setUsernameState(editUsername.trim());
                setIsEditingUsername(false);
                setSaveStatus('saved');
                onProfileUpdate?.();
                setTimeout(() => setSaveStatus('idle'), 2000);
            } catch (err) {
                console.error('Failed to save username:', err);
                setSaveStatus('idle');
            }
        }
    };

    const handleSaveUuid = async () => {
        const trimmed = editUuid.trim();
        if (trimmed) {
            setSaveStatus('saving');
            try {
                await SetUUID(trimmed);
                setUuid(trimmed);
                setIsEditingUuid(false);
                setSaveStatus('saved');
                onProfileUpdate?.();
                setTimeout(() => setSaveStatus('idle'), 2000);
            } catch (err) {
                console.error('Failed to save UUID:', err);
                setSaveStatus('idle');
            }
        }
    };

    const handleRandomizeUuid = () => {
        const newUuid = generateUUID();
        setEditUuid(newUuid);
    };

    const handleCopyUuid = async () => {
        try {
            await navigator.clipboard.writeText(uuid);
            setCopiedUuid(true);
            setTimeout(() => setCopiedUuid(false), 2000);
        } catch (err) {
            console.error('Failed to copy UUID:', err);
        }
    };

    const handleUsernameKeyDown = (e: React.KeyboardEvent) => {
        if (e.key === 'Enter') {
            handleSaveUsername();
        } else if (e.key === 'Escape') {
            setEditUsername(username);
            setIsEditingUsername(false);
        }
    };

    const handleUuidKeyDown = (e: React.KeyboardEvent) => {
        if (e.key === 'Enter') {
            handleSaveUuid();
        } else if (e.key === 'Escape') {
            setEditUuid(uuid);
            setIsEditingUuid(false);
        }
    };

    if (!isOpen) return null;

    return (
        <AnimatePresence>
            <motion.div
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                exit={{ opacity: 0 }}
                className="fixed inset-0 z-[200] flex items-center justify-center bg-black/60 backdrop-blur-sm"
                onClick={(e) => e.target === e.currentTarget && onClose()}
            >
                <motion.div
                    initial={{ opacity: 0, scale: 0.95, y: 20 }}
                    animate={{ opacity: 1, scale: 1, y: 0 }}
                    exit={{ opacity: 0, scale: 0.95, y: 20 }}
                    className="bg-[#1a1a1a] border border-white/10 rounded-2xl shadow-2xl w-full max-w-lg mx-4 overflow-hidden"
                >
                    {/* Header */}
                    <div className="flex items-center justify-between p-4 border-b border-white/10">
                        <div className="flex items-center gap-3">
                            <User size={24} style={{ color: accentColor }} />
                            <h2 className="text-lg font-bold text-white">{t('Profile Editor')}</h2>
                        </div>
                        <button
                            onClick={onClose}
                            className="w-8 h-8 rounded-lg flex items-center justify-center text-white/60 hover:text-white hover:bg-white/10 transition-colors"
                        >
                            <X size={18} />
                        </button>
                    </div>

                    {/* Content */}
                    <div className="p-6">
                        {isLoading ? (
                            <div className="flex items-center justify-center py-12">
                                <RefreshCw size={32} className="animate-spin text-white/40" />
                            </div>
                        ) : (
                            <div className="space-y-6">
                                {/* Profile Picture Section */}
                                <div className="flex flex-col items-center gap-4">
                                    {/* Skin Head from Auth Server */}
                                    <div 
                                        className="w-24 h-24 rounded-full overflow-hidden border-2 bg-[#151515]"
                                        style={{ borderColor: accentColor }}
                                        title={t('Your character head from the skin customizer')}
                                    >
                                        {uuid ? (
                                            <iframe
                                                src={getAvatarHeadUrl()}
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
                                    
                                    {/* Username Display/Edit */}
                                    <div className="flex items-center gap-2">
                                        {isEditingUsername ? (
                                            <div className="flex items-center gap-2">
                                                <input
                                                    type="text"
                                                    value={editUsername}
                                                    onChange={(e) => setEditUsername(e.target.value)}
                                                    onKeyDown={handleUsernameKeyDown}
                                                    maxLength={16}
                                                    autoFocus
                                                    className="bg-[#151515] text-white text-xl font-bold px-3 py-1 rounded-lg border outline-none w-40 text-center"
                                                    style={{ borderColor: accentColor }}
                                                />
                                                <motion.button
                                                    whileHover={{ scale: 1.1 }}
                                                    whileTap={{ scale: 0.9 }}
                                                    onClick={handleSaveUsername}
                                                    className="p-2 rounded-lg"
                                                    style={{ backgroundColor: `${accentColor}33`, color: accentColor }}
                                                >
                                                    <Check size={16} />
                                                </motion.button>
                                            </div>
                                        ) : (
                                            <>
                                                <span className="text-2xl font-bold text-white">{username}</span>
                                                <motion.button
                                                    whileHover={{ scale: 1.1 }}
                                                    whileTap={{ scale: 0.9 }}
                                                    onClick={() => {
                                                        setEditUsername(username);
                                                        setIsEditingUsername(true);
                                                    }}
                                                    className="p-1.5 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/5"
                                                    title={t('Edit Username')}
                                                >
                                                    <Edit3 size={14} />
                                                </motion.button>
                                            </>
                                        )}
                                    </div>
                                </div>

                                {/* Skin Customizer Link */}
                                <div className="p-4 rounded-xl bg-[#151515] border border-white/10">
                                    <label className="block text-sm text-white/60 mb-2">{t('Character Appearance')}</label>
                                    <p className="text-xs text-white/40 mb-3">
                                        {t('Customize your character\'s skin, hair, clothes, and accessories using the online customizer.')}
                                    </p>
                                    <motion.button
                                        whileHover={{ scale: 1.02 }}
                                        whileTap={{ scale: 0.98 }}
                                        onClick={() => BrowserOpenURL(getCustomizerUrl())}
                                        disabled={!uuid}
                                        className="w-full h-12 px-4 rounded-xl flex items-center justify-center gap-2 text-white font-medium transition-all disabled:opacity-50 disabled:cursor-not-allowed"
                                        style={{ backgroundColor: accentColor }}
                                    >
                                        <User size={18} />
                                        {t('Open Skin Customizer')}
                                        <ExternalLink size={14} className="ml-1" />
                                    </motion.button>
                                </div>

                                {/* UUID Section */}
                                <div className="p-4 rounded-xl bg-[#151515] border border-white/10">
                                    <div className="flex items-center justify-between mb-2">
                                        <label className="text-sm text-white/60">{t('Player UUID')}</label>
                                        <div className="flex items-center gap-1">
                                            <motion.button
                                                whileHover={{ scale: 1.05 }}
                                                whileTap={{ scale: 0.95 }}
                                                onClick={handleCopyUuid}
                                                className="p-1.5 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/10"
                                                title={t('Copy UUID')}
                                            >
                                                {copiedUuid ? <CheckCircle size={14} className="text-green-400" /> : <Copy size={14} />}
                                            </motion.button>
                                            <motion.button
                                                whileHover={{ scale: 1.05 }}
                                                whileTap={{ scale: 0.95 }}
                                                onClick={() => {
                                                    setEditUuid(uuid);
                                                    setIsEditingUuid(true);
                                                }}
                                                className="p-1.5 rounded-lg text-white/40 hover:text-white/80 hover:bg-white/10"
                                                title={t('Edit UUID')}
                                            >
                                                <Edit3 size={14} />
                                            </motion.button>
                                        </div>
                                    </div>
                                    
                                    {isEditingUuid ? (
                                        <div className="flex items-center gap-2">
                                            <input
                                                type="text"
                                                value={editUuid}
                                                onChange={(e) => setEditUuid(e.target.value)}
                                                onKeyDown={handleUuidKeyDown}
                                                autoFocus
                                                className="flex-1 bg-[#0a0a0a] text-white font-mono text-sm px-3 py-2 rounded-lg border outline-none"
                                                style={{ borderColor: accentColor }}
                                            />
                                            <motion.button
                                                whileHover={{ scale: 1.05 }}
                                                whileTap={{ scale: 0.95 }}
                                                onClick={handleRandomizeUuid}
                                                className="p-2 rounded-lg bg-white/10 text-white/70 hover:bg-white/20"
                                                title={t('Generate Random UUID')}
                                            >
                                                <Shuffle size={16} />
                                            </motion.button>
                                            <motion.button
                                                whileHover={{ scale: 1.05 }}
                                                whileTap={{ scale: 0.95 }}
                                                onClick={handleSaveUuid}
                                                className="p-2 rounded-lg"
                                                style={{ backgroundColor: `${accentColor}33`, color: accentColor }}
                                                title={t('Save UUID')}
                                            >
                                                <Check size={16} />
                                            </motion.button>
                                        </div>
                                    ) : (
                                        <p className="text-white font-mono text-sm truncate">{uuid}</p>
                                    )}
                                </div>

                                {/* Save Status */}
                                {saveStatus === 'saved' && (
                                    <motion.div
                                        initial={{ opacity: 0, y: 10 }}
                                        animate={{ opacity: 1, y: 0 }}
                                        exit={{ opacity: 0 }}
                                        className="flex items-center justify-center gap-2 text-green-400 text-sm"
                                    >
                                        <CheckCircle size={16} />
                                        {t('Profile saved!')}
                                    </motion.div>
                                )}
                            </div>
                        )}
                    </div>
                </motion.div>
            </motion.div>
        </AnimatePresence>
    );
};

// Keep the old export name for backwards compatibility
export const SkinEditor = ProfileEditor;

export default ProfileEditor;
